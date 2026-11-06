using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Runtime.Versioning;
using Microsoft.Win32.SafeHandles;
using KamenRaider.Agent.Leases;

namespace KamenRaider.Agent.Sunshine;

public interface IStreamGuardPipeServerFactory
{
    NamedPipeServerStream CreateServer();
}

public static class StreamGuardPipeSecurity
{
    [SupportedOSPlatform("windows")]
    public static PipeSecurity CreateAgentPipeSecurity(SecurityIdentifier agentServiceSid)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("Windows pipe ACLs are available only on Windows.");
        ArgumentNullException.ThrowIfNull(agentServiceSid);
        var security = new PipeSecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.AddAccessRule(new PipeAccessRule(
            new SecurityIdentifier(WellKnownSidType.NetworkSid, null),
            PipeAccessRights.FullControl,
            AccessControlType.Deny));
        security.AddAccessRule(new PipeAccessRule(
            new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
            PipeAccessRights.FullControl,
            AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(agentServiceSid, PipeAccessRights.ReadWrite, AccessControlType.Allow));
        return security;
    }
}

[SupportedOSPlatform("windows")]
public sealed class WindowsStreamGuardPipeServerFactory : IStreamGuardPipeServerFactory
{
    private const uint PipeAccessDuplex = 0x00000003;
    private const uint FileFlagOverlapped = 0x40000000;
    private const uint FileFlagFirstPipeInstance = 0x00080000;
    private const uint PipeTypeByte = 0x00000000;
    private const uint PipeReadModeByte = 0x00000000;
    private const uint PipeWait = 0x00000000;
    private const uint PipeRejectRemoteClients = 0x00000008;
    private readonly SecurityIdentifier agentServiceSid;
    private readonly string pipeName;

    public WindowsStreamGuardPipeServerFactory(string serviceName = "KamenRaider Agent")
        : this(ServiceSidResolver.Resolve(serviceName), StreamGuardPipeClient.DefaultPipeName)
    {
    }

    public WindowsStreamGuardPipeServerFactory(SecurityIdentifier agentServiceSid, string pipeName = StreamGuardPipeClient.DefaultPipeName)
    {
        ArgumentNullException.ThrowIfNull(agentServiceSid);
        ArgumentException.ThrowIfNullOrWhiteSpace(pipeName);
        this.agentServiceSid = agentServiceSid;
        this.pipeName = pipeName;
    }

    public NamedPipeServerStream CreateServer()
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("The Stream Guard named pipe requires Windows.");

        var securityDescriptor = StreamGuardPipeSecurity.CreateAgentPipeSecurity(agentServiceSid).GetSecurityDescriptorBinaryForm();
        var descriptorPin = GCHandle.Alloc(securityDescriptor, GCHandleType.Pinned);
        try
        {
            var securityAttributes = new SecurityAttributes
            {
                Length = Marshal.SizeOf<SecurityAttributes>(),
                SecurityDescriptor = descriptorPin.AddrOfPinnedObject(),
                InheritHandle = false
            };
            var openMode = PipeAccessDuplex | FileFlagOverlapped | FileFlagFirstPipeInstance;
            var pipeMode = PipeTypeByte | PipeReadModeByte | PipeWait | PipeRejectRemoteClients;
            var nativePipe = CreateNamedPipeW(
                $@"\\.\pipe\{pipeName}",
                openMode,
                pipeMode,
                1,
                StreamGuardPipeProtocol.MaxFrameBytes,
                StreamGuardPipeProtocol.MaxFrameBytes,
                0,
                ref securityAttributes);

            if (nativePipe.IsInvalid)
            {
                var error = Marshal.GetLastPInvokeError();
                nativePipe.Dispose();
                throw new System.ComponentModel.Win32Exception(error, "Unable to create the protected local Stream Guard pipe.");
            }

            try
            {
                return new NamedPipeServerStream(PipeDirection.InOut, isAsync: true, isConnected: false, nativePipe);
            }
            catch
            {
                nativePipe.Dispose();
                throw;
            }
        }
        finally
        {
            descriptorPin.Free();
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SecurityAttributes
    {
        public int Length;
        public IntPtr SecurityDescriptor;
        [MarshalAs(UnmanagedType.Bool)] public bool InheritHandle;
    }

    [DllImport("kernel32.dll", EntryPoint = "CreateNamedPipeW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafePipeHandle CreateNamedPipeW(
        string name,
        uint openMode,
        uint pipeMode,
        uint maximumInstances,
        uint outputBufferSize,
        uint inputBufferSize,
        uint defaultTimeout,
        ref SecurityAttributes securityAttributes);
}

internal static class ServiceSidResolver
{
    private static readonly Regex ServiceSidPattern = new(@"SERVICE SID:\s*(S-1-5-80(?:-\d+){5})", RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    public static SecurityIdentifier Resolve(string serviceName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(serviceName);
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("Windows service SIDs are available only on Windows.");

        var scPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "sc.exe");
        var startInfo = new ProcessStartInfo
        {
            FileName = scPath,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        startInfo.ArgumentList.Add("showsid");
        startInfo.ArgumentList.Add(serviceName);
        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("Unable to start Service Control Manager SID lookup.");

        if (!process.WaitForExit(5000))
        {
            process.Kill(entireProcessTree: true);
            throw new TimeoutException("Service Control Manager SID lookup timed out.");
        }

        var output = process.StandardOutput.ReadToEnd() + Environment.NewLine + process.StandardError.ReadToEnd();
        var match = ServiceSidPattern.Match(output);
        if (process.ExitCode != 0 || !match.Success)
            throw new InvalidOperationException($"Unable to resolve the service SID for '{serviceName}'.");
        return new SecurityIdentifier(match.Groups[1].Value);
    }
}

public sealed class StreamGuardPipeRequestHandler(IStreamGuardControl gate)
{
    public async Task HandleSessionAsync(Stream pipe, CancellationToken cancellationToken)
    {
        var seenRequestIds = new HashSet<string>(StringComparer.Ordinal);
        while (true)
        {
            byte[]? frame;
            try
            {
                frame = await StreamGuardPipeProtocol.ReadFrameAsync(pipe, cancellationToken);
            }
            catch (InvalidDataException)
            {
                await gate.BlockAsync(CancellationToken.None);
                return;
            }

            if (frame is null) return;

            StreamGuardPipeRequest request;
            try
            {
                request = StreamGuardPipeProtocol.DecodeRequest(frame);
            }
            catch (JsonException)
            {
                await gate.BlockAsync(CancellationToken.None);
                return;
            }
            catch (InvalidDataException)
            {
                await gate.BlockAsync(CancellationToken.None);
                return;
            }

            if (!Guid.TryParseExact(request.RequestId, "N", out _) || !seenRequestIds.Add(request.RequestId))
            {
                await RejectAndCloseAsync(pipe, request.RequestId, "invalid_request_id", cancellationToken);
                return;
            }

            if (request.Version != StreamGuardPipeProtocol.Version)
            {
                await RejectAndCloseAsync(pipe, request.RequestId, "unsupported_version", cancellationToken);
                return;
            }

            if (request.Command == "block" && request.Grant is null && request.Scope is null)
            {
                try
                {
                    await gate.BlockAsync(cancellationToken);
                    await WriteResponseAsync(pipe, request.RequestId, true, null, cancellationToken);
                }
                catch (Exception blockError)
                {
                    try
                    {
                        await gate.BlockAsync(CancellationToken.None);
                    }
                    catch (Exception closeError)
                    {
                        throw new AggregateException("The Stream Guard block request failed and closed state could not be verified.", blockError, closeError);
                    }

                    await WriteResponseAsync(pipe, request.RequestId, false, "gate_rejected", cancellationToken);
                    return;
                }
                continue;
            }

            if (request.Command == "allow" && request.Grant is { } grant && request.Scope is null)
            {
                try
                {
                    await gate.AllowAsync(grant, cancellationToken);
                    await WriteResponseAsync(pipe, request.RequestId, true, null, cancellationToken);
                }
                catch
                {
                    await gate.BlockAsync(CancellationToken.None);
                    await WriteResponseAsync(pipe, request.RequestId, false, "gate_rejected", cancellationToken);
                    return;
                }
                continue;
            }

            if (request.Command == "verify-open" && request.Grant is null && request.Scope is { } scope)
            {
                try
                {
                    var verified = await gate.VerifyOpenForLeaseAsync(scope, cancellationToken);
                    await WriteResponseAsync(pipe, request.RequestId, true, verified ? null : "lease_not_open", cancellationToken, verified);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch
                {
                    await gate.BlockAsync(CancellationToken.None);
                    await WriteResponseAsync(pipe, request.RequestId, true, "gate_unverified", cancellationToken, false);
                }
                continue;
            }

            if (request.Command == "maintenance-begin" && request.Grant is null && request.Scope is null)
            {
                try
                {
                    await gate.BeginFencedMaintenanceAsync(cancellationToken);
                    await WriteResponseAsync(pipe, request.RequestId, true, null, cancellationToken);
                }
                catch
                {
                    await gate.BlockAsync(CancellationToken.None);
                    await WriteResponseAsync(pipe, request.RequestId, false, "maintenance_rejected", cancellationToken);
                    return;
                }
                continue;
            }

            if (request.Command == "maintenance-end" && request.Grant is null && request.Scope is null)
            {
                try
                {
                    var ended = await gate.EndFencedMaintenanceAsync(cancellationToken);
                    await WriteResponseAsync(pipe, request.RequestId, ended, ended ? null : "maintenance_expired", cancellationToken);
                }
                catch
                {
                    await gate.BlockAsync(CancellationToken.None);
                    await WriteResponseAsync(pipe, request.RequestId, false, "maintenance_end_failed", cancellationToken);
                    return;
                }
                continue;
            }

            await RejectAndCloseAsync(pipe, request.RequestId, "invalid_request", cancellationToken);
            return;
        }
    }

    private async Task RejectAndCloseAsync(Stream pipe, string requestId, string error, CancellationToken cancellationToken)
    {
        await gate.BlockAsync(CancellationToken.None);
        await WriteResponseAsync(pipe, requestId, false, error, cancellationToken);
    }

    private static Task WriteResponseAsync(Stream pipe, string requestId, bool success, string? error, CancellationToken cancellationToken, bool? verified = null) =>
        StreamGuardPipeProtocol.WriteFrameAsync(
            pipe,
            new StreamGuardPipeResponse(StreamGuardPipeProtocol.Version, requestId, success, error, verified),
            cancellationToken);
}

public sealed class StreamGuardPipeSession(StreamGuardPipeRequestHandler handler, IStreamGate gate)
{
    public async Task RunAsync(Stream pipe, CancellationToken cancellationToken)
    {
        try
        {
            await handler.HandleSessionAsync(pipe, cancellationToken);
        }
        finally
        {
            await gate.BlockAsync(CancellationToken.None);
        }
    }
}

public sealed class StreamGuardPipeServer(
    IStreamGuardPipeServerFactory pipeFactory,
    StreamGuardPipeRequestHandler handler,
    IStreamGate gate,
    ILogger<StreamGuardPipeServer> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await gate.BlockAsync(CancellationToken.None);
        try
        {
            var session = new StreamGuardPipeSession(handler, gate);
            while (!stoppingToken.IsCancellationRequested)
            {
                await using var pipe = pipeFactory.CreateServer();
                await pipe.WaitForConnectionAsync(stoppingToken);
                logger.LogInformation("Stream Guard accepted its local Agent connection.");
                try
                {
                    await session.RunAsync(pipe, stoppingToken);
                }
                catch (IOException ex) when (!stoppingToken.IsCancellationRequested)
                {
                    logger.LogWarning(ex, "Stream Guard pipe disconnected during a request; Sunshine was closed.");
                }
                logger.LogInformation("Stream Guard Agent connection ended; Sunshine was closed.");
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
        finally
        {
            await gate.BlockAsync(CancellationToken.None);
        }
    }
}

public sealed class StreamGuardDeadlineWorker(StreamGateCoordinator gate, ILogger<StreamGuardDeadlineWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(250));
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                try
                {
                    await gate.EnforceAsync(stoppingToken);
                }
                catch (InvalidOperationException ex)
                {
                    logger.LogError(ex, "Stream Guard detected an unverifiable state and closed Sunshine.");
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }
}

using System.IO.Pipes;
using System.Text.Json;
using KamenRaider.Agent.Leases;

namespace KamenRaider.Agent.Sunshine;

public sealed class StreamGuardPipeClient : IStreamGate, IPairingFence, IAsyncDisposable
{
    public const string DefaultPipeName = "KamenRaider.StreamGuard.v2";
    private static readonly TimeSpan DefaultConnectTimeout = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan DefaultRequestTimeout = TimeSpan.FromSeconds(5);
    private readonly string pipeName;
    private readonly TimeSpan connectTimeout;
    private readonly TimeSpan requestTimeout;
    private readonly SemaphoreSlim serial = new(1, 1);
    private readonly SemaphoreSlim operationSerial = new(1, 1);
    private NamedPipeClientStream? pipe;
    private bool disposed;

    public StreamGuardPipeClient(
        string pipeName = DefaultPipeName,
        TimeSpan? connectTimeout = null,
        TimeSpan? requestTimeout = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pipeName);
        this.pipeName = pipeName;
        this.connectTimeout = PositiveTimeout(connectTimeout ?? DefaultConnectTimeout, nameof(connectTimeout));
        this.requestTimeout = PositiveTimeout(requestTimeout ?? DefaultRequestTimeout, nameof(requestTimeout));
    }

    public Task BlockAsync(CancellationToken cancellationToken)
    {
        // Closing is containment; a caller cancellation must not prevent notifying the guard.
        _ = cancellationToken;
        return SendSerializedAsync("block", null, CancellationToken.None);
    }

    public async Task AllowAsync(StreamLeaseGrant grant, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(grant);
        await operationSerial.WaitAsync(cancellationToken);
        try { await SendAsync("allow", grant, cancellationToken); }
        finally { operationSerial.Release(); }
    }

    public async Task<bool> VerifyOpenForLeaseAsync(PairingScope scope, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(scope);
        await operationSerial.WaitAsync(cancellationToken);
        try
        {
            var response = await SendRequestAsync("verify-open", null, scope, cancellationToken);
            return response.Success && response.Verified == true;
        }
        finally { operationSerial.Release(); }
    }

    public async Task<bool> RunFencedCleanupAsync(Func<CancellationToken, Task> clear, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(clear);
        await operationSerial.WaitAsync(cancellationToken);
        try
        {
            await SendAsync("maintenance-begin", null, cancellationToken);
            try
            {
                await clear(cancellationToken);
            }
            finally
            {
                var end = await SendRequestAsync("maintenance-end", null, null, CancellationToken.None);
                if (!end.Success)
                {
                    await ResetPipeAsync();
                    throw new InvalidOperationException($"Stream Guard could not finish fenced cleanup ({end.Error ?? "unspecified"}).");
                }
            }

            return true;
        }
        finally
        {
            operationSerial.Release();
        }
    }

    private async Task SendSerializedAsync(string command, StreamLeaseGrant? grant, CancellationToken cancellationToken)
    {
        await operationSerial.WaitAsync(CancellationToken.None);
        try { await SendAsync(command, grant, cancellationToken); }
        finally { operationSerial.Release(); }
    }

    private async Task SendAsync(string command, StreamLeaseGrant? grant, CancellationToken cancellationToken)
    {
        var response = await SendRequestAsync(command, grant, null, cancellationToken);
        if (response.Success) return;

        await ResetPipeAsync();
        throw new InvalidOperationException($"Stream Guard rejected the request ({response.Error ?? "unspecified"}).");
    }

    private async Task<StreamGuardPipeResponse> SendRequestAsync(string command, StreamLeaseGrant? grant, PairingScope? scope, CancellationToken cancellationToken)
    {
        await serial.WaitAsync(cancellationToken);
        try
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            var request = new StreamGuardPipeRequest(
                StreamGuardPipeProtocol.Version,
                Guid.NewGuid().ToString("N"),
                command,
                grant,
                scope);

            for (var attempt = 0; attempt < 2; attempt++)
            {
                StreamGuardPipeResponse response;
                using var requestTimeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                requestTimeoutSource.CancelAfter(requestTimeout);

                try
                {
                    await EnsureConnectedAsync(requestTimeoutSource.Token);
                    var connectedPipe = pipe ?? throw new IOException("Stream Guard pipe is not connected.");
                    await StreamGuardPipeProtocol.WriteFrameAsync(connectedPipe, request, requestTimeoutSource.Token);
                    var frame = await StreamGuardPipeProtocol.ReadFrameAsync(connectedPipe, requestTimeoutSource.Token);
                    if (frame is null)
                        throw new EndOfStreamException("Stream Guard closed the connection before acknowledging the request.");
                    response = StreamGuardPipeProtocol.DecodeResponse(frame);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    await ResetPipeAsync();
                    throw;
                }
                catch (Exception ex) when (IsTransportFailure(ex))
                {
                    await ResetPipeAsync();
                    if (attempt == 1)
                        throw new InvalidOperationException("Stream Guard did not acknowledge the request.", ex);
                    continue;
                }
                catch (JsonException ex)
                {
                    await ResetPipeAsync();
                    throw new InvalidOperationException("Stream Guard returned a malformed acknowledgment.", ex);
                }

                if (response.Version != StreamGuardPipeProtocol.Version || response.RequestId != request.RequestId)
                {
                    await ResetPipeAsync();
                    throw new InvalidOperationException("Stream Guard returned an acknowledgment for another protocol version or request.");
                }

                return response;
            }
            throw new InvalidOperationException("Stream Guard did not acknowledge the request.");
        }
        finally
        {
            serial.Release();
        }
    }

    private async Task EnsureConnectedAsync(CancellationToken cancellationToken)
    {
        if (pipe is { IsConnected: true }) return;
        await ResetPipeAsync();

        var candidate = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        using var connectTimeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        connectTimeoutSource.CancelAfter(connectTimeout);
        try
        {
            await candidate.ConnectAsync(connectTimeoutSource.Token);
            pipe = candidate;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            await candidate.DisposeAsync();
            throw new TimeoutException("Timed out connecting to Stream Guard.");
        }
        catch
        {
            await candidate.DisposeAsync();
            throw;
        }
    }

    private async Task ResetPipeAsync()
    {
        var oldPipe = pipe;
        pipe = null;
        if (oldPipe is not null)
            await oldPipe.DisposeAsync();
    }

    private static bool IsTransportFailure(Exception error) =>
        error is IOException or TimeoutException or UnauthorizedAccessException or ObjectDisposedException or OperationCanceledException;

    private static TimeSpan PositiveTimeout(TimeSpan value, string parameterName)
    {
        if (value <= TimeSpan.Zero || value > TimeSpan.FromMinutes(1))
            throw new ArgumentOutOfRangeException(parameterName, "Timeouts must be greater than zero and no more than one minute.");
        return value;
    }

    public async ValueTask DisposeAsync()
    {
        await operationSerial.WaitAsync(CancellationToken.None);
        try
        {
            await serial.WaitAsync(CancellationToken.None);
            try
            {
                if (disposed) return;
                disposed = true;
                await ResetPipeAsync();
            }
            finally { serial.Release(); }
        }
        finally { operationSerial.Release(); }
    }
}

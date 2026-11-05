using System.IO.Pipes;
using System.Text.Json;
using KamenRaider.Agent.Leases;

namespace KamenRaider.Agent.Sunshine;

public sealed class StreamGuardPipeClient : IStreamGate, IAsyncDisposable
{
    public const string DefaultPipeName = "KamenRaider.StreamGuard.v1";
    private static readonly TimeSpan DefaultConnectTimeout = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan DefaultRequestTimeout = TimeSpan.FromSeconds(5);
    private readonly string pipeName;
    private readonly TimeSpan connectTimeout;
    private readonly TimeSpan requestTimeout;
    private readonly SemaphoreSlim serial = new(1, 1);
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
        return SendAsync("block", null, CancellationToken.None);
    }

    public Task AllowAsync(StreamLeaseGrant grant, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(grant);
        return SendAsync("allow", grant, cancellationToken);
    }

    private async Task SendAsync(string command, StreamLeaseGrant? grant, CancellationToken cancellationToken)
    {
        await serial.WaitAsync(cancellationToken);
        try
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            var request = new StreamGuardPipeRequest(
                StreamGuardPipeProtocol.Version,
                Guid.NewGuid().ToString("N"),
                command,
                grant);

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

                if (!response.Success)
                {
                    await ResetPipeAsync();
                    throw new InvalidOperationException($"Stream Guard rejected the request ({response.Error ?? "unspecified"}).");
                }

                return;
            }
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
        await serial.WaitAsync(CancellationToken.None);
        try
        {
            if (disposed) return;
            disposed = true;
            await ResetPipeAsync();
        }
        finally
        {
            serial.Release();
        }
    }
}

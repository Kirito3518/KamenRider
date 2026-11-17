using System.Net;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using KamenRaider.AgentMaintenance;
using KamenRaider.Contracts;

namespace Controller.Tests;

public sealed class MaintenanceHttpDeadlineTests
{
    private const string MachineId = "0123456789abcdef0123456789abcdef";

    private sealed class IdentitySource(TrustedIdentityDocument document) : ITrustedIdentitySource
    {
        public TrustedIdentityDocument ReadExisting() => document;
    }

    private sealed class LifecycleWorkStore(MaintenanceLifecycleRequest request) : IMaintenanceLifecycleWorkStore
    {
        public MaintenanceLifecycleRequest ReadLifecycleRequest(string expectedCommand)
        {
            Assert.Equal("start", expectedCommand);
            return request;
        }
    }

    private sealed class JournalStore : IMaintenanceJournalStore
    {
        public MaintenanceJournalSnapshot? Snapshot { get; private set; }
        public MaintenanceJournalSnapshot? Read(string chainId) => Snapshot;
        public void WriteAtomic(string chainId, MaintenanceJournalSnapshot snapshot) => Snapshot = snapshot;
    }

    private sealed class ExclusionLease : IMaintenanceApplyProcessExclusionLease
    {
        public bool Disposed { get; private set; }
        public void VerifyContinuouslyHeld(string operationId) => Assert.Equal("op-1", operationId);
        public void Dispose() => Disposed = true;
    }

    private sealed class ExclusionProvider(ExclusionLease lease) : IMaintenanceApplyProcessExclusionProvider
    {
        public IMaintenanceApplyProcessExclusionLease Acquire() => lease;
    }

    private sealed class StallingHandler(StallingContent content) : HttpMessageHandler
    {
        public StallingContent Content { get; } = content;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = Content });
    }

    private sealed class StallingContent : HttpContent
    {
        public StallingStream Body { get; } = new();

        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return false;
        }

        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) =>
            throw new NotSupportedException();

        protected override Task<Stream> CreateContentReadStreamAsync() => Task.FromResult<Stream>(Body);
    }

    private sealed class StallingStream : Stream
    {
        public TaskCompletionSource ReadStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource CancellationObserved { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            ReadStarted.TrySetResult();
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                return 0;
            }
            catch (OperationCanceledException)
            {
                CancellationObserved.TrySetResult();
                throw;
            }
        }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    [Fact]
    public async Task LifecycleClientDeadlineCancelsAStalledBodyAfterHeaders()
    {
        var handler = new StallingHandler(new StallingContent());
        using var http = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        var client = new HttpMaintenanceLifecycleClient(http, TimeSpan.FromMilliseconds(150));

        var error = await Record.ExceptionAsync(() => client.StartAsync("https://controller.example/", MachineId,
            "op-1", new AgentUpdateTicketRequest(new string('T', 43), "2.3.4", "2.3.4", new string('b', 64), "start-1"),
            CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(3)));

        Assert.IsType<TimeoutException>(error);
        await handler.Content.Body.ReadStarted.Task.WaitAsync(TimeSpan.FromSeconds(1));
        await handler.Content.Body.CancellationObserved.Task.WaitAsync(TimeSpan.FromSeconds(1));
    }

    [Fact]
    public async Task StartCommandReleasesItsExclusionWhenTheLifecycleBodyDeadlineExpires()
    {
        const string machineId = MachineId;
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var identitySource = new IdentitySource(new(machineId, Convert.ToBase64String(key.ExportPkcs8PrivateKey())));
        string fingerprint;
        using (var identity = TrustedIdentity.ReadExisting(identitySource))
            fingerprint = identity.KeyFingerprint;
        var runtime = RuntimeInformation.OSArchitecture == Architecture.Arm64 ? "win-arm64" : "win-x64";
        var request = new MaintenanceLifecycleRequest(1, "start", "op-1", "chain-1", "op-1", 1,
            "https://controller.example/", fingerprint, 1, "2.3.4", runtime, new string('b', 64),
            "2.3.4", new string('T', 43));
        var journal = new JournalStore();
        var lease = new ExclusionLease();
        var handler = new StallingHandler(new StallingContent());
        using var http = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        var client = new HttpMaintenanceLifecycleClient(http, TimeSpan.FromMilliseconds(150));
        var command = new MaintenanceStartCommand(new LifecycleWorkStore(request), identitySource, journal, client,
            new ExclusionProvider(lease));

        var error = await Record.ExceptionAsync(() => Task.Run(command.Execute).WaitAsync(TimeSpan.FromSeconds(3)));

        Assert.IsType<TimeoutException>(error);
        await handler.Content.Body.CancellationObserved.Task.WaitAsync(TimeSpan.FromSeconds(1));
        Assert.True(lease.Disposed);
        Assert.Equal("inspected", journal.Snapshot!.Operations.Single().Phase);
    }

    [Fact]
    public async Task LifecycleClientPreservesCallerCancellation()
    {
        var handler = new StallingHandler(new StallingContent());
        using var http = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        var client = new HttpMaintenanceLifecycleClient(http, TimeSpan.FromSeconds(10));
        using var callerCancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(150));

        var error = await Record.ExceptionAsync(() => client.StartAsync("https://controller.example/", MachineId,
            "op-1", new AgentUpdateTicketRequest(new string('T', 43), "2.3.4", "2.3.4", new string('b', 64), "start-1"),
            callerCancellation.Token).WaitAsync(TimeSpan.FromSeconds(3)));

        Assert.IsAssignableFrom<OperationCanceledException>(error);
        Assert.IsNotType<TimeoutException>(error);
        await handler.Content.Body.ReadStarted.Task.WaitAsync(TimeSpan.FromSeconds(1));
        await handler.Content.Body.CancellationObserved.Task.WaitAsync(TimeSpan.FromSeconds(1));
    }

    [Fact]
    public async Task SourceBindingClientDeadlineCancelsAStalledBodyAfterHeaders()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var identity = TrustedIdentity.ReadExisting(new IdentitySource(new(MachineId,
            Convert.ToBase64String(key.ExportPkcs8PrivateKey()))));
        var handler = new StallingHandler(new StallingContent());
        using var http = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        var client = new HttpMaintenanceSourceBindingClient(http, TimeSpan.FromMilliseconds(150));
        var request = new AgentUpdateSourceChallengeRequest(new string('T', 43), "2.3.4", "2.3.4",
            new string('b', 64), "challenge-request-1", new string('c', 64), "win-x64", 1);

        var error = await Record.ExceptionAsync(() => client.IssueChallengeAsync("https://controller.example/", MachineId,
            "op-1", request, identity, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(3)));

        Assert.IsType<TimeoutException>(error);
        await handler.Content.Body.ReadStarted.Task.WaitAsync(TimeSpan.FromSeconds(1));
        await handler.Content.Body.CancellationObserved.Task.WaitAsync(TimeSpan.FromSeconds(1));
    }

    [Fact]
    public async Task SourceBindingClientPreservesCallerCancellation()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var identity = TrustedIdentity.ReadExisting(new IdentitySource(new(MachineId,
            Convert.ToBase64String(key.ExportPkcs8PrivateKey()))));
        var handler = new StallingHandler(new StallingContent());
        using var http = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        var client = new HttpMaintenanceSourceBindingClient(http, TimeSpan.FromSeconds(10));
        var request = new AgentUpdateSourceChallengeRequest(new string('T', 43), "2.3.4", "2.3.4",
            new string('b', 64), "challenge-request-1", new string('c', 64), "win-x64", 1);
        using var callerCancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(150));

        var error = await Record.ExceptionAsync(() => client.IssueChallengeAsync("https://controller.example/", MachineId,
            "op-1", request, identity, callerCancellation.Token).WaitAsync(TimeSpan.FromSeconds(3)));

        Assert.IsAssignableFrom<OperationCanceledException>(error);
        Assert.IsNotType<TimeoutException>(error);
        await handler.Content.Body.ReadStarted.Task.WaitAsync(TimeSpan.FromSeconds(1));
        await handler.Content.Body.CancellationObserved.Task.WaitAsync(TimeSpan.FromSeconds(1));
    }
}

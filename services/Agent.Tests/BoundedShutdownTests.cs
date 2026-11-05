using KamenRaider.Agent.Power;

namespace Agent.Tests;

public class BoundedShutdownTests
{
    private sealed class StalledShutdown : IShutdown
    {
        public CancellationToken RequestToken { get; private set; }
        public TaskCompletionSource Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task RequestImmediateShutdownAsync(CancellationToken ct)
        {
            RequestToken = ct;
            return Completion.Task;
        }
    }

    [Fact]
    public async Task TimeoutCancelsTheUnderlyingShutdownRequest()
    {
        var inner = new StalledShutdown();
        var shutdown = new BoundedShutdown(inner, TimeSpan.FromMilliseconds(25));
        var request = shutdown.RequestImmediateShutdownAsync(default);

        try
        {
            await Assert.ThrowsAsync<TimeoutException>(() => request.WaitAsync(TimeSpan.FromSeconds(1)));
            Assert.True(inner.RequestToken.IsCancellationRequested);
        }
        finally
        {
            inner.Completion.TrySetResult();
        }
    }
}

namespace KamenRaider.Agent.Power;

public sealed class BoundedShutdown(IShutdown inner, TimeSpan timeout) : IShutdown
{
    public async Task RequestImmediateShutdownAsync(CancellationToken ct)
    {
        if (timeout <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(timeout));

        using var deadline = new CancellationTokenSource(timeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, deadline.Token);
        var request = inner.RequestImmediateShutdownAsync(linked.Token);

        try
        {
            await request.WaitAsync(linked.Token);
        }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested && !ct.IsCancellationRequested)
        {
            linked.Cancel();
            _ = ObserveCompletionAsync(request);
            throw new TimeoutException("Immediate shutdown request timed out.");
        }
        catch
        {
            linked.Cancel();
            _ = ObserveCompletionAsync(request);
            throw;
        }
    }

    private static async Task ObserveCompletionAsync(Task request)
    {
        try { await request; }
        catch (Exception) { }
    }
}

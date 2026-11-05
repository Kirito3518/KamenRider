namespace KamenRaider.Agent.Startup;
public static class BootSyncBarrier {
 public static async Task RunAsync(Func<Task<Readiness>> reconcile,Func<Readiness,Task> sync){var readiness=await reconcile();await sync(readiness);}
}

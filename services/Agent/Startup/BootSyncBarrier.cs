namespace KamenRaider.Agent.Startup;
public static class BootSyncBarrier {
 public static async Task RunAsync(Func<Task<Readiness>> reconcile,Func<Task> sync) {if(await reconcile()==Readiness.Ready)await sync();}
}

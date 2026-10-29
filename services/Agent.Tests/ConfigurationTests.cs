using KamenRaider.Agent.Configuration;
using KamenRaider.Contracts;
namespace Agent.Tests;
public class ConfigurationTests {
 [Fact] public async Task SavesAtomicallyAndIgnoresOlderRevision() {var dir=Path.Combine(Path.GetTempPath(),Guid.NewGuid().ToString());Directory.CreateDirectory(dir);try {var store=new AppliedConfiguration(dir);await store.ApplyAsync(new(3,5000,6000),default);await store.ApplyAsync(new(1,1,2),default);var loaded=new AppliedConfiguration(dir);Assert.Equal(3,loaded.Current!.Revision);Assert.Equal(5000,loaded.Current.MoonlightPort);Assert.Single(Directory.GetFiles(dir));}finally{Directory.Delete(dir,true);}}
}

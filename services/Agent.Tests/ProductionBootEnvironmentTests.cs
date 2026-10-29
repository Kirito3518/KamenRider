using KamenRaider.Agent.Startup;
using KamenRaider.Contracts;
namespace Agent.Tests;
public class ProductionBootEnvironmentTests {
 [Fact] public async Task MissingHardwareEvidenceCannotAffirmRestorationFenceOrHealth() {
  var environment=new UnavailableBootEnvironment();var challenge=new ReconciliationChallenge("token","boot",null,null,DateTimeOffset.UtcNow.AddMinutes(1));
  Assert.False(await environment.VerifyRestorationAsync(challenge,default));Assert.False(await environment.FenceAndClearAsync(challenge,default));Assert.False(await environment.VerifyHealthAsync(challenge,default));Assert.False(await environment.RestoreLeaseAsync(new ActiveLeaseConfirmation("rental",1,"boot",DateTimeOffset.UtcNow.AddMinutes(1)),DateTimeOffset.UtcNow,0,default));
 }
}

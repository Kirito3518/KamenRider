using System.Net;
using KamenRaider.Agent.Sunshine;
namespace Agent.Tests;
public class SunshineTests {
 [Fact] public async Task UnverifiedGateNeverListsOrPairs() {
  var api=new Api();var scope=new PairingScope("lease",1,"boot",DateTimeOffset.UtcNow.AddMinutes(1),60000);
  var pairing=new PairingSession(api,new UnverifiedStreamGate(),()=>scope,TimeProvider.System);
  Assert.Empty(await pairing.ListAsync(default));
  Assert.False(await pairing.PairAsync("id","1234",default));Assert.Equal(0,api.Calls);
 }
 [Fact] public async Task PairingApiRunsOnlyAfterVerifyingTheCurrentOpenLeaseWithoutClosingSunshine() {
  var events=new List<string>();var api=new Api(events);var scope=new PairingScope("lease",1,"boot",DateTimeOffset.UtcNow.AddMinutes(1),60000);
  var pairing=new PairingSession(api,new Fence(true,events),()=>scope,TimeProvider.System);
  Assert.Equal(new[]{"id","second"},await pairing.ListAsync(default));
  Assert.True(await pairing.PairAsync("id","1234",default));
  Assert.Equal(new[]{"verify-open:lease:1:boot","pending-list","verify-open:lease:1:boot","submit:id"},events);
 }
 [Fact] public async Task FailedOpenLeaseVerificationDoesNotCallSunshineOrOfferPairing() {
  var api=new Api();var scope=new PairingScope("lease",1,"boot",DateTimeOffset.UtcNow.AddMinutes(1),60000);
  var pairing=new PairingSession(api,new ThrowingFence(),()=>scope,TimeProvider.System);
  Assert.Empty(await pairing.ListAsync(default));
  Assert.False(await pairing.PairAsync("id","1234",default));
  Assert.Equal(0,api.Calls);
 }
 [Fact] public async Task PairingScopeWithoutAMonotonicDeadlineIsRejectedBeforeGuardOrSunshineCalls() {
  var events=new List<string>();var api=new Api(events);var scope=new PairingScope("lease",1,"boot",DateTimeOffset.UtcNow.AddMinutes(1),0);
  var pairing=new PairingSession(api,new Fence(true,events),()=>scope,TimeProvider.System);
  Assert.Empty(await pairing.ListAsync(default));
  Assert.Empty(events);
 }
 [Fact] public async Task ClientCleanupDelegatesToFencedMaintenanceBoundary() {
  var events=new List<string>();var api=new Api(events);var scope=new PairingScope("lease",1,"boot",DateTimeOffset.UtcNow.AddMinutes(1),60000);
  var pairing=new PairingSession(api,new Fence(true,events),()=>scope,TimeProvider.System);
  Assert.True(await pairing.ClearAsync(default));
  Assert.Equal(new[]{"closed-fence-maintenance","clear-clients"},events);
 }
 [Theory][InlineData("123")][InlineData("12345")][InlineData("１２３４")][InlineData("abcd")]
 public async Task InvalidPinNeverSent(string pin) {
  var api=new Api();var scope=new PairingScope("lease",1,"boot",DateTimeOffset.UtcNow.AddMinutes(1),60000);
  var pairing=new PairingSession(api,new Fence(true),()=>scope,TimeProvider.System);
  await pairing.ListAsync(default);Assert.False(await pairing.PairAsync("id",pin,default));Assert.Equal(0,api.PairCalls);
 }
 [Fact] public async Task SelectionMustHaveBeenOfferedInSameLeaseAndBoot() {
  var api=new Api();var scope=new PairingScope("lease",1,"boot",DateTimeOffset.UtcNow.AddMinutes(1),60000);
  var pairing=new PairingSession(api,new Fence(true),()=>scope,TimeProvider.System);
  await pairing.ListAsync(default);Assert.False(await pairing.PairAsync("other","1234",default));
  scope=scope with {BootId="next"};Assert.False(await pairing.PairAsync("id","1234",default));Assert.Equal(0,api.PairCalls);
 }
 [Fact] public async Task ExplicitSelectionUsesIdAndCannotBeReplayed() {
  var api=new Api();var scope=new PairingScope("lease",1,"boot",DateTimeOffset.UtcNow.AddMinutes(1),60000);
  var pairing=new PairingSession(api,new Fence(true),()=>scope,TimeProvider.System);
  Assert.Equal(new[]{"id","second"},await pairing.ListAsync(default));
  Assert.True(await pairing.PairAsync("second","1234",default));Assert.Equal("second",api.Selected);
  Assert.False(await pairing.PairAsync("second","1234",default));
 }
 [Fact] public async Task CleanupFailurePermanentlyClosesSession() {
  var api=new Api{FailCleanup=true};var scope=new PairingScope("lease",1,"boot",DateTimeOffset.UtcNow.AddMinutes(1),60000);
  var pairing=new PairingSession(api,new Fence(true),()=>scope,TimeProvider.System);
  await pairing.ListAsync(default);Assert.False(await pairing.ClearAsync(default));
  Assert.Empty(await pairing.ListAsync(default));Assert.False(await pairing.PairAsync("id","1234",default));
 }
 [Fact] public async Task CleanupCancelsInFlightPairAndDoesNotAcknowledgeLateSuccess() {
  var api=new BlockingApi();var scope=new PairingScope("lease",1,"boot",DateTimeOffset.UtcNow.AddMinutes(1),60000);
  var pairing=new PairingSession(api,new Fence(true),()=>scope,TimeProvider.System);
  await pairing.ListAsync(default);var pair=pairing.PairAsync("id","1234",default);await api.Started.Task;
  var cleanup=pairing.ClearAsync(default);
  await api.Cancelled.Task.WaitAsync(TimeSpan.FromSeconds(2));api.Release.SetResult();
  Assert.False(await pair);Assert.True(await cleanup);Assert.True(api.Cleared);
 }
 [Theory][InlineData("http://127.0.0.1:47990")][InlineData("https://example.com:47990")][InlineData("https://localhost:47990/remote")][InlineData("https://user:pass@localhost:47990")]
 public void ManagementEndpointMustBeLocalHttpsOrigin(string url)=>Assert.Throws<ArgumentException>(()=>new SunshineClient(new Uri(url),new NetworkCredential("u","p")));
 [Fact] public async Task AuthFailureIsNotReadyAndPendingListFails() {
  using var api=new SunshineClient(new Uri("https://127.0.0.1:47990"),new NetworkCredential("u","p"),new Transport(HttpStatusCode.Unauthorized,"{}"));
  Assert.False(await api.CheckReadyAsync(default));await Assert.ThrowsAsync<HttpRequestException>(()=>api.PendingIdsAsync(default));
 }
 private sealed class Transport(HttpStatusCode status,string json):HttpMessageHandler {
  public string Json=json;public string? Url,Auth,Body;
  protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken ct){Url=request.RequestUri!.ToString();Auth=request.Headers.Authorization?.ToString();Body=request.Content is null?null:await request.Content.ReadAsStringAsync(ct);return new(status){Content=new StringContent(Json)};}
 }
 [Fact] public async Task ExpiredCommandNeverSendsPinAndSuccessfulReplayDoesNotSendTwice(){var api=new Api();var scope=new PairingScope("lease",1,"boot",DateTimeOffset.UtcNow.AddMinutes(1),60000);var session=new PairingSession(api,new Fence(true),()=>scope,TimeProvider.System);await session.ListAsync(default);
  var command=new KamenRaider.Contracts.CommandEnvelope("cmd","PairPin","lease",1,"boot",DateTimeOffset.UtcNow.AddSeconds(20),System.Text.Json.JsonSerializer.SerializeToElement(new{pairingId="id",pin="1234"}));Assert.Equal("failed",(await session.ExecuteAsync(command with{ValidUntilUtc=DateTimeOffset.UtcNow.AddSeconds(-1)},default)).Status);Assert.Equal(0,api.PairCalls);
  Assert.Equal("applied",(await session.ExecuteAsync(command,default)).Status);Assert.Equal("applied",(await session.ExecuteAsync(command,default)).Status);Assert.Equal(1,api.PairCalls);
 }
 [Fact] public async Task ScopeChangeDuringHandshakeRequiresCleanupBeforeNextOffer(){var api=new BlockingApi();var scope=new PairingScope("lease",1,"boot",DateTimeOffset.UtcNow.AddMinutes(1),60000);var session=new PairingSession(api,new Fence(true),()=>scope,TimeProvider.System);await session.ListAsync(default);var pair=session.PairAsync("id","1234",default);await api.Started.Task;scope=scope with{RentalId="next"};api.Release.SetResult();Assert.False(await pair);Assert.Empty(await session.ListAsync(default));}
 [Fact] public async Task OfferExpiresByMonotonicTimeEvenWhenWallClockRollsBack(){var api=new Api();var clock=new PairClock();var scope=new PairingScope("lease",1,"boot",clock.Now.AddMinutes(1),60000);var session=new PairingSession(api,new Fence(true),()=>scope,clock);await session.ListAsync(default);clock.Ticks=31;clock.Now=clock.Now.AddDays(-1);Assert.False(await session.PairAsync("id","1234",default));Assert.Equal(0,api.PairCalls);}
 [Fact] public async Task GateWaitCannotExtendOfferLifetime(){var api=new Api();var clock=new PairClock();var scope=new PairingScope("lease",1,"boot",clock.Now.AddMinutes(1),60000);var fence=new PausingFence();var session=new PairingSession(api,fence,()=>scope,clock);await session.ListAsync(default);var pair=session.PairAsync("id","1234",default);await fence.Entered.Task;clock.Ticks=31;clock.Now=clock.Now.AddSeconds(31);fence.Release.SetResult();Assert.False(await pair);Assert.Equal(0,api.PairCalls);}
 private sealed class PausingFence:IPairingFence {private int calls;public TaskCompletionSource Entered=new(TaskCreationOptions.RunContinuationsAsynchronously),Release=new(TaskCreationOptions.RunContinuationsAsynchronously);public async Task<bool> VerifyOpenForLeaseAsync(PairingScope scope,CancellationToken ct){if(++calls==2){Entered.SetResult();await Release.Task;}return true;}public async Task<bool> RunFencedCleanupAsync(Func<CancellationToken,Task> clear,CancellationToken ct){await clear(ct);return true;}}
 private sealed class PairClock:TimeProvider {public DateTimeOffset Now=DateTimeOffset.Parse("2026-10-30T00:00:00Z");public long Ticks;public override DateTimeOffset GetUtcNow()=>Now;public override long GetTimestamp()=>Ticks;public override long TimestampFrequency=>1;}
 [Theory][InlineData("timeout")][InlineData("caller")][InlineData("transport")][InlineData("cancelled-success")]
 public async Task AmbiguousSubmissionRequiresCleanupBeforeAnyNewOffer(string failure){
  using var caller=new CancellationTokenSource();var api=new AmbiguousApi(failure,caller);var scope=new PairingScope("lease",1,"boot",DateTimeOffset.UtcNow.AddMinutes(1),60000);var session=new PairingSession(api,new Fence(true),()=>scope,TimeProvider.System);
  await session.ListAsync(default);await Record.ExceptionAsync(()=>session.PairAsync("id","1234",caller.Token));
  Assert.Empty(await session.ListAsync(default));scope=scope with{RentalId="next"};Assert.Empty(await session.ListAsync(default));
  Assert.True(await session.ClearAsync(default));Assert.Equal(new[]{"id"},await session.ListAsync(default));
 }
 [Theory][InlineData("{}")][InlineData("not json")][InlineData("{\"status\":true}")]
 public async Task MalformedSubmissionResponseIsAmbiguousAndRequiresCleanup(string json){
  var id=SunshineClient.PairingSlotId;var transport=new SubmissionTransport(json);using var api=new SunshineClient(new Uri("https://localhost:47990"),new NetworkCredential("u","p"),transport);var scope=new PairingScope("lease",1,"boot",DateTimeOffset.UtcNow.AddMinutes(1),60000);var session=new PairingSession(api,new Fence(true),()=>scope,TimeProvider.System);await session.ListAsync(default);
  await Assert.ThrowsAsync<InvalidDataException>(()=>session.PairAsync(id,"1234",default));Assert.Empty(await session.ListAsync(default));Assert.True(await session.ClearAsync(default));Assert.Equal(new[]{id},await session.ListAsync(default));
 }
 private sealed class SubmissionTransport(string submission):HttpMessageHandler {
  protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken ct){var json=request.RequestUri!.AbsolutePath switch {"/api/clients/list"=>"{\"status\":\"true\",\"named_certs\":[]}","/api/pin"=>submission,_=>"{\"status\":\"true\"}"};return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK){Content=new StringContent(json)});}
 }
 private sealed class AmbiguousApi(string failure,CancellationTokenSource caller):ISunshinePairingApi {
  public Task<string[]> PendingIdsAsync(CancellationToken ct)=>Task.FromResult(new[]{"id"});
  public Task<bool> SubmitAsync(string id,string pin,CancellationToken ct){
   if(failure is "caller" or "cancelled-success")caller.Cancel();
   if(failure=="cancelled-success")return Task.FromResult(true);
   return Task.FromException<bool>(failure=="transport"?new HttpRequestException("Lost response after acceptance"):new OperationCanceledException());
  }
  public Task ClearAsync(CancellationToken ct)=>Task.CompletedTask;
 }
 private sealed class BlockingApi:ISunshinePairingApi {
  public TaskCompletionSource Started=new(TaskCreationOptions.RunContinuationsAsynchronously),Cancelled=new(TaskCreationOptions.RunContinuationsAsynchronously),Release=new(TaskCreationOptions.RunContinuationsAsynchronously);public bool Cleared;
  public Task<string[]> PendingIdsAsync(CancellationToken ct)=>Task.FromResult(new[]{"id"});
  public async Task<bool> SubmitAsync(string id,string pin,CancellationToken ct){using var registration=ct.Register(()=>Cancelled.TrySetResult());Started.SetResult();await Release.Task;return true;}
  public Task ClearAsync(CancellationToken ct){Cleared=true;return Task.CompletedTask;}
 }
 private sealed class Fence(bool verified,List<string>? events=null):IPairingFence {
  public Task<bool> VerifyOpenForLeaseAsync(PairingScope scope,CancellationToken ct){events?.Add($"verify-open:{scope.RentalId}:{scope.Revision}:{scope.BootId}");return Task.FromResult(verified);}
  public async Task<bool> RunFencedCleanupAsync(Func<CancellationToken,Task> clear,CancellationToken ct){
   if(!verified)return false;events?.Add("closed-fence-maintenance");await clear(ct);return true;
  }
 }
 private sealed class ThrowingFence:IPairingFence {
  public Task<bool> VerifyOpenForLeaseAsync(PairingScope scope,CancellationToken ct)=>throw new IOException("guard unavailable");
  public Task<bool> RunFencedCleanupAsync(Func<CancellationToken,Task> clear,CancellationToken ct)=>Task.FromResult(false);
 }
 private sealed class Api:ISunshinePairingApi {
  private readonly List<string>? events;public Api(List<string>? events=null)=>this.events=events;
  public int Calls,PairCalls;public string? Selected;public bool FailCleanup;
  public Task<string[]> PendingIdsAsync(CancellationToken ct){Calls++;events?.Add("pending-list");return Task.FromResult(new[]{"id","second"});}
  public Task<bool> SubmitAsync(string id,string pin,CancellationToken ct){Calls++;PairCalls++;events?.Add($"submit:{id}");Selected=id;return Task.FromResult(true);}
  public Task ClearAsync(CancellationToken ct){Calls++;events?.Add("clear-clients");if(FailCleanup)throw new IOException();return Task.CompletedTask;}
 }
}




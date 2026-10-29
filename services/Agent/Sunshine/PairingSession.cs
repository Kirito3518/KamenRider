namespace KamenRaider.Agent.Sunshine;
public record PairingScope(string RentalId,long Revision,string BootId,DateTimeOffset ExpiresAtUtc);
// Must prove ingress AND existing/in-flight handshakes are fenced; firewall rule creation alone is insufficient.
public interface IPairingFence {Task<bool> BlockAndVerifyAsync(CancellationToken ct);}
public interface ISunshinePairingApi {
 Task<string[]> PendingIdsAsync(CancellationToken ct);
 Task<bool> SubmitAsync(string id,string pin,CancellationToken ct);
 Task ClearAsync(CancellationToken ct);
}
public sealed class PairingSession(ISunshinePairingApi api,IPairingFence fence,Func<PairingScope?> scope,TimeProvider time) {
 private readonly SemaphoreSlim serial=new(1,1);
 private PairingScope? offeredScope;private string[] offered=[];private DateTimeOffset offerExpiry;private long offerTimestamp;private volatile bool failed;private readonly object lifecycle=new();private long generation;private CancellationTokenSource? activePair;
 private bool Valid(PairingScope? value)=>value is { Revision:>0 } && !string.IsNullOrWhiteSpace(value.RentalId)&&!string.IsNullOrWhiteSpace(value.BootId)&&value.ExpiresAtUtc>time.GetUtcNow();
 private readonly SemaphoreSlim commandSerial=new(1,1);
 private readonly Dictionary<string,(PairingScope Scope,string Id,DateTimeOffset Until,KamenRaider.Contracts.CommandResult Result)> completed=[];
 public async Task<KamenRaider.Contracts.CommandResult> ExecuteAsync(KamenRaider.Contracts.CommandEnvelope command,CancellationToken ct){await commandSerial.WaitAsync(ct);try{
  var rejected=new KamenRaider.Contracts.CommandResult(command.CommandId,"failed",command.LeaseRevision,"PairingRejected");var current=scope();
  if(command.Type!="PairPin"||!Valid(current)||command.RentalId!=current!.RentalId||command.LeaseRevision!=current.Revision||command.BootId!=current.BootId||command.ValidUntilUtc<=time.GetUtcNow())return rejected;
  string id,pin;try{id=command.Payload.GetProperty("pairingId").GetString()??"";pin=command.Payload.GetProperty("pin").GetString()??"";}catch(Exception ex) when(ex is System.Text.Json.JsonException or KeyNotFoundException or InvalidOperationException){return rejected;}
  foreach(var key in completed.Where(x=>x.Value.Until<=time.GetUtcNow()).Select(x=>x.Key).ToArray())completed.Remove(key);
  if(completed.TryGetValue(command.CommandId,out var previous))return previous.Scope==current&&previous.Id==id?previous.Result:rejected;
  if(completed.Count>=100)return rejected;
  using var timeout=CancellationTokenSource.CreateLinkedTokenSource(ct);timeout.CancelAfter(command.ValidUntilUtc-time.GetUtcNow());
  bool success;try{success=await PairAsync(id,pin,timeout.Token);}catch(OperationCanceledException) when(!ct.IsCancellationRequested){success=false;}
  var result=success&&command.ValidUntilUtc>time.GetUtcNow()?new KamenRaider.Contracts.CommandResult(command.CommandId,"applied",command.LeaseRevision):rejected;
  completed[command.CommandId]=(current,id,command.ValidUntilUtc,result);return result;
 }finally{commandSerial.Release();}}
 public async Task<KamenRaider.Contracts.PairingOffer?> CreateOfferAsync(CancellationToken ct){await ListAsync(ct);await serial.WaitAsync(ct);try{return !failed&&Valid(offeredScope)&&offeredScope==scope()&&time.GetUtcNow()<offerExpiry?new(offeredScope!.RentalId,offeredScope.Revision,offeredScope.BootId,offerExpiry<offeredScope.ExpiresAtUtc?offerExpiry:offeredScope.ExpiresAtUtc,offered.ToArray()):null;}finally{serial.Release();}}
 public async Task<string[]> ListAsync(CancellationToken ct){await serial.WaitAsync(ct);try{
  offered=[];offeredScope=null;var current=scope();
  if(failed||!Valid(current)||!await fence.BlockAndVerifyAsync(ct))return [];
  var ids=await api.PendingIdsAsync(ct);
  if(current!=scope()||!Valid(current)||ids.Any(string.IsNullOrWhiteSpace)||ids.Distinct(StringComparer.Ordinal).Count()!=ids.Length)return [];
  offeredScope=current;offerExpiry=time.GetUtcNow().AddSeconds(30);offerTimestamp=time.GetTimestamp();offered=ids.ToArray();return ids.ToArray();
 }finally{serial.Release();}}
 public async Task<bool> PairAsync(string id,string pin,CancellationToken ct){await serial.WaitAsync(ct);try{
  var current=scope();if(failed||pin is null||pin.Length!=4||pin.Any(c=>c<'0'||c>'9')||!Valid(current)||current!=offeredScope||(time.GetUtcNow()>=offerExpiry||time.GetElapsedTime(offerTimestamp)>=TimeSpan.FromSeconds(30)||time.GetElapsedTime(offerTimestamp)<TimeSpan.Zero)||!offered.Contains(id,StringComparer.Ordinal))return false;
  offered=[];offeredScope=null;
  if(!await fence.BlockAndVerifyAsync(ct))return false;
  if(current!=scope()||!Valid(current)||time.GetUtcNow()>=offerExpiry||time.GetElapsedTime(offerTimestamp)>=TimeSpan.FromSeconds(30)||time.GetElapsedTime(offerTimestamp)<TimeSpan.Zero)return false;
    using var linked=CancellationTokenSource.CreateLinkedTokenSource(ct);long started;
  lock(lifecycle){if(failed)return false;started=generation;activePair=linked;}
  var submissionStarted=false;var unambiguous=false;
  try{linked.Token.ThrowIfCancellationRequested();submissionStarted=true;var result=await api.SubmitAsync(id,pin,linked.Token);if(linked.IsCancellationRequested||current!=scope()||!Valid(current))return false;unambiguous=true;return result&&!failed&&started==Volatile.Read(ref generation);}
  catch(OperationCanceledException) when(!ct.IsCancellationRequested){return false;}
  finally{if(submissionStarted&&!unambiguous)failed=true;lock(lifecycle){if(ReferenceEquals(activePair,linked))activePair=null;}}
 }finally{serial.Release();}}
  public async Task<bool> ClearAsync(CancellationToken ct){long clearing;
  lock(lifecycle){failed=true;clearing=++generation;activePair?.Cancel();}
  await serial.WaitAsync(ct);try{
  failed=true;offered=[];offeredScope=null;
  if(!await fence.BlockAndVerifyAsync(ct))return false;
  try{await api.ClearAsync(ct);lock(lifecycle){if(clearing!=generation)return false;failed=false;}return true;}catch(Exception ex) when(ex is not OperationCanceledException){return false;}
 }finally{serial.Release();}}
}


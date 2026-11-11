using KamenRaider.Contracts;
using KamenRaider.Controller.Data;
using KamenRaider.Controller.Security;
using KamenRaider.Controller.Network;
using System.Text.Json;
using System.Text;
using Microsoft.Extensions.Options;
namespace KamenRaider.Controller.Agents;
public static class SyncEndpoint {
 public static void MapSync(this WebApplication app) {
 app.MapPost("/agent/sync",async (HttpContext context,ControllerDatabase db,AgentAuthentication auth)=> {
 var id=context.Request.Headers["X-Agent-Id"].ToString();var key=db.PublicKey(id);
 if(key is null)return Results.Unauthorized();
 using var memory=new MemoryStream();await context.Request.Body.CopyToAsync(memory,context.RequestAborted);var bytes=memory.ToArray();
 var verified=auth.VerifySignature(id,key,"POST","/agent/sync",context.Request.Headers["X-Agent-Timestamp"].ToString(),context.Request.Headers["X-Agent-Nonce"].ToString(),bytes,context.Request.Headers["X-Agent-Signature"].ToString());
 if(verified is null)return Results.Unauthorized();
 string body;try{body=new UTF8Encoding(false,true).GetString(bytes);}catch(DecoderFallbackException){return Results.BadRequest();}
 try {
  var request=JsonSerializer.Deserialize<SyncRequest>(body,new JsonSerializerOptions(JsonSerializerDefaults.Web));if(request is null)return Results.BadRequest();
  var decision=db.Sync(verified,request);
  if(decision.StatusCode==428)return Results.Json(new SyncChallengeRequired(decision.Challenge!),statusCode:428);
  if(decision.StatusCode==200)return Results.Ok(decision.Response);
  if(decision.StatusCode==429)context.Response.Headers.RetryAfter="10";
  return Results.StatusCode(decision.StatusCode);
 }catch(Exception ex) when(ex is ArgumentException or JsonException){return Results.BadRequest();}
 });
 app.MapPost("/agent/reconcile",async (HttpContext context,ControllerDatabase db,AgentAuthentication auth)=> {
 var id=context.Request.Headers["X-Agent-Id"].ToString();var key=db.PublicKey(id);
 if(key is null)return Results.Unauthorized();
 using var memory=new MemoryStream();await context.Request.Body.CopyToAsync(memory,context.RequestAborted);var bytes=memory.ToArray();
 var verified=auth.VerifySignature(id,key,"POST","/agent/reconcile",context.Request.Headers["X-Agent-Timestamp"].ToString(),context.Request.Headers["X-Agent-Nonce"].ToString(),bytes,context.Request.Headers["X-Agent-Signature"].ToString());
 if(verified is null)return Results.Unauthorized();
 string body;try{body=new UTF8Encoding(false,true).GetString(bytes);}catch(DecoderFallbackException){return Results.BadRequest();}
 try {var request=JsonSerializer.Deserialize<ReconcileRequest>(body,new JsonSerializerOptions(JsonSerializerDefaults.Web));return request is null?Results.BadRequest():Results.Ok(db.Reconcile(verified,request));}
 catch(UnauthorizedAccessException){return Results.Unauthorized();}
 catch(Exception ex) when(ex is ArgumentException or JsonException or KeyNotFoundException){return Results.BadRequest();}
 });
 app.MapPost("/internal/machines/{id}/wake",async (string id,ControllerDatabase db,IWakeSender sender,IOptions<ControllerOptions> options,CancellationToken ct)=> {
 var machine=db.List().SingleOrDefault(m=>m.MachineId==id);if(machine is null)return Results.NotFound();
 try {await sender.SendAsync(machine.MacAddress,options.Value.WakeBroadcastAddress,ct);return Results.Ok(new {status="requested"});}catch(Exception ex) when(ex is ArgumentException or FormatException or System.Net.Sockets.SocketException){return Results.Problem("Wake network configuration or delivery unavailable",statusCode:503);}
 });
 }
}

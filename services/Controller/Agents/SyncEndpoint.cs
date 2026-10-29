using KamenRaider.Contracts;
using KamenRaider.Controller.Data;
using KamenRaider.Controller.Security;
using KamenRaider.Controller.Network;
using System.Text.Json;
using Microsoft.Extensions.Options;
namespace KamenRaider.Controller.Agents;
public static class SyncEndpoint {
 public static void MapSync(this WebApplication app) {
 app.MapPost("/agent/sync",async (HttpContext context,ControllerDatabase db,AgentAuthentication auth)=> {
 var id=context.Request.Headers["X-Agent-Id"].ToString();var key=db.PublicKey(id);
 if(key is null||!long.TryParse(context.Request.Headers["X-Agent-Timestamp"],out var seconds))return Results.Unauthorized();
 DateTimeOffset timestamp;try{timestamp=DateTimeOffset.FromUnixTimeSeconds(seconds);}catch(ArgumentOutOfRangeException){return Results.Unauthorized();}
 using var reader=new StreamReader(context.Request.Body);var body=await reader.ReadToEndAsync(context.RequestAborted);
 if(!auth.Verify(id,key,"POST","/agent/sync",timestamp,context.Request.Headers["X-Agent-Nonce"].ToString(),body,context.Request.Headers["X-Agent-Signature"].ToString(),DateTimeOffset.UtcNow))return Results.Unauthorized();
 try {var request=JsonSerializer.Deserialize<SyncRequest>(body,new JsonSerializerOptions(JsonSerializerDefaults.Web));return request is null?Results.BadRequest():Results.Ok(db.Sync(id,request));}catch(Exception ex) when(ex is ArgumentException or JsonException){return Results.BadRequest();}
 });
 app.MapPost("/agent/reconcile",async (HttpContext context,ControllerDatabase db,AgentAuthentication auth)=> {
 var id=context.Request.Headers["X-Agent-Id"].ToString();var key=db.PublicKey(id);
 if(key is null||!long.TryParse(context.Request.Headers["X-Agent-Timestamp"],out var seconds))return Results.Unauthorized();
 DateTimeOffset timestamp;try{timestamp=DateTimeOffset.FromUnixTimeSeconds(seconds);}catch(ArgumentOutOfRangeException){return Results.Unauthorized();}
 using var reader=new StreamReader(context.Request.Body);var body=await reader.ReadToEndAsync(context.RequestAborted);
 if(!auth.Verify(id,key,"POST","/agent/reconcile",timestamp,context.Request.Headers["X-Agent-Nonce"].ToString(),body,context.Request.Headers["X-Agent-Signature"].ToString(),DateTimeOffset.UtcNow))return Results.Unauthorized();
 try {var request=JsonSerializer.Deserialize<ReconcileRequest>(body,new JsonSerializerOptions(JsonSerializerDefaults.Web));return request is null?Results.BadRequest():Results.Ok(db.Reconcile(id,request));}catch(Exception ex) when(ex is ArgumentException or JsonException or KeyNotFoundException){return Results.BadRequest();}
 });
 app.MapPost("/internal/machines/{id}/wake",async (string id,ControllerDatabase db,IWakeSender sender,IOptions<ControllerOptions> options,CancellationToken ct)=> {
 var machine=db.List().SingleOrDefault(m=>m.MachineId==id);if(machine is null)return Results.NotFound();
 try {await sender.SendAsync(machine.MacAddress,options.Value.WakeBroadcastAddress,ct);return Results.Ok(new {status="requested"});}catch(Exception ex) when(ex is ArgumentException or FormatException or System.Net.Sockets.SocketException){return Results.Problem("Wake network configuration or delivery unavailable",statusCode:503);}
 });
 }
}

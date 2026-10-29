using KamenRaider.Contracts;
using KamenRaider.Controller.Data;
namespace KamenRaider.Controller.Leases;
public static class PairingEndpoint {
 public static void MapPairing(this WebApplication app){
  app.MapGet("/internal/leases/{id}/pair/pending",(string id,HttpContext context,ControllerDatabase db)=>{
   try{return Results.Ok(db.GetPairingOffer(id,context.Request.Headers["X-Authenticated-Actor-Id"].ToString()));}
   catch(UnauthorizedAccessException){return Results.StatusCode(403);}catch(KeyNotFoundException){return Results.NotFound();}
  });
  app.MapPost("/internal/leases/{id}/pair",(string id,PairingRequest request,HttpContext context,ControllerDatabase db)=>{
   try{return Results.Ok(db.QueuePairing(id,context.Request.Headers["X-Authenticated-Actor-Id"].ToString(),request));}
   catch(UnauthorizedAccessException){return Results.StatusCode(403);}catch(KeyNotFoundException){return Results.NotFound();}catch(ArgumentException){return Results.BadRequest(new{error="Invalid pairing request"});}catch(InvalidOperationException){return Results.Conflict(new{error="Pairing unavailable or stale selection"});}
  });
  app.MapGet("/internal/leases/{id}/pair/{commandId}",(string id,string commandId,HttpContext context,ControllerDatabase db)=>{
   try{return Results.Ok(db.PairingStatus(id,commandId,context.Request.Headers["X-Authenticated-Actor-Id"].ToString()));}
   catch(UnauthorizedAccessException){return Results.StatusCode(403);}catch(KeyNotFoundException){return Results.NotFound();}
  });
 }
}

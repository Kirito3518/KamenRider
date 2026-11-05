using KamenRaider.Contracts;
using KamenRaider.Controller.Data;
using KamenRaider.Controller.Security;
using System.Text.Json;
namespace KamenRaider.Controller.Machines;
public static class MachineEndpoints
{
    public static void MapMachines(this WebApplication app) {
        app.MapGet("/internal/machines",(ControllerDatabase db)=>db.List());
        app.MapPost("/internal/agent-enrollment-tickets",(ControllerDatabase db)=>Results.Ok(db.CreateEnrollmentTicket()));
        app.MapPatch("/internal/machines/{id}",(string id,MachineUpdate update,long expectedRevision,ControllerDatabase db)=>Execute(()=>db.Update(id,update,expectedRevision)));
        app.MapPost("/internal/machines/{id}/approve",(string id,MachineIdentityConfirmation confirmation,HttpContext context,ControllerDatabase db)=>Execute(()=>db.ApproveVerified(id,context.Request.Headers["X-Authenticated-Actor-Id"].ToString(),confirmation.Fingerprint)));
        app.MapPost("/internal/machines/{id}/reject",(string id,MachineIdentityConfirmation confirmation,ControllerDatabase db)=>Execute(()=>db.RejectPending(id,confirmation.Fingerprint)));
        app.MapPost("/agent/enroll",async (HttpContext context,ControllerDatabase db,AgentAuthentication auth)=> {
            using var reader=new StreamReader(context.Request.Body);var body=await reader.ReadToEndAsync();
            EnrollmentRequest? request;
            try {request=JsonSerializer.Deserialize<EnrollmentRequest>(body,new JsonSerializerOptions(JsonSerializerDefaults.Web));} catch(JsonException){return Results.BadRequest();}
            if(request is null)return Results.BadRequest();
            if(!long.TryParse(context.Request.Headers["X-Agent-Timestamp"],out var seconds))return Results.Unauthorized();
            DateTimeOffset timestamp;try {timestamp=DateTimeOffset.FromUnixTimeSeconds(seconds);}catch(ArgumentOutOfRangeException){return Results.Unauthorized();}
            // Enrollment proves possession even for a previously approved ID. Its submitted key never authenticates another route.
            if(!auth.Verify(request.MachineId,request.PublicKey,"POST","/agent/enroll",timestamp,context.Request.Headers["X-Agent-Nonce"].ToString(),body,context.Request.Headers["X-Agent-Signature"].ToString(),DateTimeOffset.UtcNow))return Results.Unauthorized();
            return Execute(()=>db.Enroll(request,context.Request.Headers["X-Enrollment-Ticket"].ToString()));
        });
    }
    private static IResult Execute<T>(Func<T> action) {
        try{return Results.Ok(action());}
        catch(EnrollmentCapacityException){return Results.StatusCode(StatusCodes.Status429TooManyRequests);}
        catch(UnauthorizedAccessException){return Results.Unauthorized();}
        catch(ArgumentException){return Results.BadRequest();}
        catch(FormatException){return Results.BadRequest();}
        catch(System.Security.Cryptography.CryptographicException){return Results.BadRequest();}
        catch(KeyNotFoundException){return Results.NotFound();}
        catch(InvalidOperationException){return Results.Conflict();}
    }
}

using KamenRaider.Contracts;
using KamenRaider.Controller.Data;
using KamenRaider.Controller.Security;
using System.Text.Json;
using System.Text;
namespace KamenRaider.Controller.Machines;
public static class MachineEndpoints
{
    private static bool IsAdmin(HttpContext context) => !string.IsNullOrWhiteSpace(context.Request.Headers["X-Authenticated-Actor-Id"])
        && context.Request.Headers["X-Authenticated-Actor-Role"] == "admin";

    public static void MapMachines(this WebApplication app) {
        app.MapGet("/internal/machines",(ControllerDatabase db)=>db.List());
        app.MapPost("/internal/agent-enrollment-tickets",(ControllerDatabase db)=>Results.Ok(db.CreateEnrollmentTicket()));
        app.MapPost("/internal/machines/{id}/agent-updates",(string id,AgentUpdateReservationRequest request,HttpContext context,ControllerDatabase db)=>
            IsAdmin(context)?Execute(()=>db.ReserveAgentUpdate(id,request.ReleaseVersion,context.Request.Headers["X-Authenticated-Actor-Id"].ToString(),request.RequestId ?? context.TraceIdentifier)):Results.StatusCode(StatusCodes.Status403Forbidden));
        app.MapPost("/internal/machines/{id}/agent-updates/{operationId}/recovery-ticket",(string id,string operationId,HttpContext context,ControllerDatabase db)=>
            IsAdmin(context)?Execute(()=>db.RenewAgentUpdateTicket(id,operationId)):Results.StatusCode(StatusCodes.Status403Forbidden));
        app.MapPatch("/internal/machines/{id}",(string id,MachineUpdate update,long expectedRevision,ControllerDatabase db)=>Execute(()=>db.Update(id,update,expectedRevision)));
        app.MapPost("/internal/machines/{id}/approve",(string id,MachineIdentityConfirmation confirmation,HttpContext context,ControllerDatabase db)=>Execute(()=>db.ApproveVerified(id,context.Request.Headers["X-Authenticated-Actor-Id"].ToString(),confirmation.Fingerprint)));
        app.MapPost("/internal/machines/{id}/reject",(string id,MachineIdentityConfirmation confirmation,ControllerDatabase db)=>Execute(()=>db.RejectPending(id,confirmation.Fingerprint)));
        app.MapPost("/agent/enroll",async (HttpContext context,ControllerDatabase db,AgentAuthentication auth)=> {
            using var memory=new MemoryStream();await context.Request.Body.CopyToAsync(memory,context.RequestAborted);var bytes=memory.ToArray();
            string body;try{body=new UTF8Encoding(false,true).GetString(bytes);}catch(DecoderFallbackException){return Results.BadRequest();}
            EnrollmentRequest? request;
            try {request=JsonSerializer.Deserialize<EnrollmentRequest>(body,new JsonSerializerOptions(JsonSerializerDefaults.Web));} catch(JsonException){return Results.BadRequest();}
            if(request is null)return Results.BadRequest();
            // Enrollment proves possession even for a previously approved ID. Its submitted key never authenticates another route.
            var verified=auth.VerifySignature(request.MachineId,request.PublicKey,"POST","/agent/enroll",context.Request.Headers["X-Agent-Timestamp"].ToString(),context.Request.Headers["X-Agent-Nonce"].ToString(),bytes,context.Request.Headers["X-Agent-Signature"].ToString());
            if(verified is null)return Results.Unauthorized();
            return Execute(()=>db.Enroll(verified,request,context.Request.Headers["X-Enrollment-Ticket"].ToString()));
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
        catch(AgentReleaseUnavailableException exception){return Results.Conflict(new { code=AgentReleaseUnavailableException.ResponseCode, available=false, reason=exception.SafeReason });}
        catch(InvalidOperationException){return Results.Conflict();}
    }
}

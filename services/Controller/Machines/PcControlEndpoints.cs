using KamenRaider.Contracts;
using KamenRaider.Controller.Data;

namespace KamenRaider.Controller.Machines;

/// <summary>
/// Admin-only PC-control endpoints. The browser never supplies an actor or a lease id: the
/// trusted Admin forwarding headers identify the actor and the machine resolves its current lease.
/// </summary>
public static class PcControlEndpoints
{
    private static bool AdminActor(HttpContext context, out string actorId)
    {
        actorId = context.Request.Headers["X-Authenticated-Actor-Id"].ToString();
        return !string.IsNullOrWhiteSpace(actorId)
            && context.Request.Headers["X-Authenticated-Actor-Role"] == "admin";
    }

    public static void MapPcControl(this WebApplication app)
    {
        app.MapGet("/internal/pc-control", (HttpContext context, ControllerDatabase database) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            if (!AdminActor(context, out _)) return Results.StatusCode(StatusCodes.Status403Forbidden);
            return Execute(() => Results.Ok(database.GetPcControlSnapshot()));
        });

        app.MapPost("/internal/machines/{id}/power-off", (string id, MachinePowerOffRequest? request,
            HttpContext context, ControllerDatabase database) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            if (!AdminActor(context, out var actorId)) return Results.StatusCode(StatusCodes.Status403Forbidden);
            if (request is null || string.IsNullOrWhiteSpace(request.RequestId) || request.RequestId.Length > 128)
                return Results.BadRequest();
            return Execute(() => Results.Ok(database.RequestMachinePowerOff(id, actorId, request.RequestId)));
        });

        app.MapGet("/internal/machines/{id}/pairing", (string id, HttpContext context, ControllerDatabase database) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            if (!AdminActor(context, out var actorId)) return Results.StatusCode(StatusCodes.Status403Forbidden);
            return Execute(() => Results.Ok(database.GetAdminPairingOffer(id, actorId)));
        });

        app.MapPost("/internal/machines/{id}/pairing", (string id, AdminPairingRequestDto? request,
            HttpContext context, ControllerDatabase database) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            if (!AdminActor(context, out var actorId)) return Results.StatusCode(StatusCodes.Status403Forbidden);
            if (request is null) return Results.BadRequest();
            return Execute(() => Results.Ok(database.QueueAdminPairing(id, actorId, request)));
        });

        app.MapGet("/internal/machines/{id}/pairing/{commandId}", (string id, string commandId,
            HttpContext context, ControllerDatabase database) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            if (!AdminActor(context, out var actorId)) return Results.StatusCode(StatusCodes.Status403Forbidden);
            return Execute(() => Results.Ok(database.GetAdminPairingStatus(id, commandId, actorId)));
        });

        app.MapPost("/internal/machines/{id}/lease-time", (string id, LeaseTimeAdjustRequestDto? request,
            HttpContext context, ControllerDatabase database) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            if (!AdminActor(context, out var actorId)) return Results.StatusCode(StatusCodes.Status403Forbidden);
            if (request is null) return Results.BadRequest();
            return Execute(() => Results.Ok(database.AdjustMachineLeaseTime(id, request.DeltaSeconds,
                request.ExpectedRevision, request.IdempotencyKey, actorId)));
        });

        app.MapPost("/internal/machines/{id}/lease-cancel", (string id, LeaseCancelRequestDto? request,
            HttpContext context, ControllerDatabase database) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            if (!AdminActor(context, out var actorId)) return Results.StatusCode(StatusCodes.Status403Forbidden);
            if (request is null) return Results.BadRequest();
            return Execute(() => Results.Ok(database.CancelMachineLease(id, request.ExpectedRevision,
                request.IdempotencyKey, request.RequestId, actorId)));
        });
    }

    private static IResult Execute(Func<IResult> action)
    {
        try { return action(); }
        catch (UnauthorizedAccessException) { return Results.StatusCode(StatusCodes.Status403Forbidden); }
        catch (KeyNotFoundException) { return Results.NotFound(); }
        catch (ArgumentException exception) when (exception is not ArgumentNullException)
        {
            return Results.BadRequest(new { error = "Invalid PC-control request" });
        }
        catch (InvalidOperationException)
        {
            return Results.Conflict(new { error = "PC-control action is not available" });
        }
    }
}

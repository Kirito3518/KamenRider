using KamenRaider.Contracts;
using KamenRaider.Controller.Data;
using KamenRaider.Controller.Security;
using System.Text;
using System.Text.Json;

namespace KamenRaider.Controller.Machines;

/// <summary>
/// Renter-facing Parsec attempt API plus the signed Agent status callback. The browser only
/// ever sends sealed ciphertext; the Controller never sees the Parsec password in plaintext.
/// </summary>
public static class ParsecEndpoints
{
    private static bool Actor(HttpContext context, out string actorId, bool admin = false)
    {
        actorId = context.Request.Headers["X-Authenticated-Actor-Id"].ToString();
        return !string.IsNullOrWhiteSpace(actorId)
            && (!admin || context.Request.Headers["X-Authenticated-Actor-Role"] == "admin");
    }

    private static IResult Execute(Func<IResult> action)
    {
        try { return action(); }
        catch (UnauthorizedAccessException) { return Results.StatusCode(StatusCodes.Status403Forbidden); }
        catch (KeyNotFoundException) { return Results.NotFound(); }
        catch (ArgumentException) { return Results.BadRequest(new { error = "Invalid Parsec request" }); }
        catch (InvalidOperationException exception) { return Results.Conflict(new { error = exception.Message }); }
    }

    public static void MapParsec(this WebApplication app)
    {
        app.MapGet("/internal/leases/{id}/parsec", (string id, HttpContext context, ControllerDatabase database) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            if (!Actor(context, out var actor)) return Results.Unauthorized();
            return Execute(() => Results.Ok(database.GetLeaseParsecState(id, actor)));
        });

        app.MapPost("/internal/leases/{id}/parsec", (string id, ParsecAttemptRequest? request,
            HttpContext context, ControllerDatabase database) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            if (!Actor(context, out var actor)) return Results.Unauthorized();
            if (request is null) return Results.BadRequest();
            return Execute(() => Results.Ok(database.QueueParsecAttempt(id, actor, request)));
        });

        app.MapGet("/internal/leases/{id}/parsec/{attemptId}", (string id, string attemptId,
            HttpContext context, ControllerDatabase database) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            if (!Actor(context, out var actor)) return Results.Unauthorized();
            return Execute(() => Results.Ok(database.GetParsecAttempt(id, attemptId, actor)));
        });

        // Admin opt-in for a machine. The Agent still has to advertise parsecd.exe capability.
        app.MapPut("/internal/machines/{id}/parsec", (string id, MachineParsecToggle? request,
            HttpContext context, ControllerDatabase database) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            if (!Actor(context, out var actor, admin: true)) return Results.StatusCode(StatusCodes.Status403Forbidden);
            if (request is null) return Results.BadRequest();
            return Execute(() => Results.Ok(database.SetMachineParsecEnabled(id, request.Enabled, actor)));
        });

        // Signed Agent status callback. The Agent reports starting/waiting_email/ready/failed
        // for one attempt; the body is authenticated exactly like the sync heartbeat.
        app.MapPost("/agent/parsec-status", async (HttpContext context, ControllerDatabase database, AgentAuthentication authentication) =>
        {
            var id = context.Request.Headers["X-Agent-Id"].ToString();
            var key = database.PublicKey(id);
            if (key is null) return Results.Unauthorized();
            using var memory = new MemoryStream();
            await context.Request.Body.CopyToAsync(memory, context.RequestAborted);
            var bytes = memory.ToArray();
            var verified = authentication.VerifySignature(id, key, "POST", "/agent/parsec-status",
                context.Request.Headers["X-Agent-Timestamp"].ToString(),
                context.Request.Headers["X-Agent-Nonce"].ToString(),
                bytes,
                context.Request.Headers["X-Agent-Signature"].ToString());
            if (verified is null) return Results.Unauthorized();
            string body;
            try { body = new UTF8Encoding(false, true).GetString(bytes); }
            catch (DecoderFallbackException) { return Results.BadRequest(); }
            try
            {
                var report = JsonSerializer.Deserialize<ParsecStatusReport>(body, new JsonSerializerOptions(JsonSerializerDefaults.Web));
                if (report is null) return Results.BadRequest();
                database.RecordParsecStatus(verified, report);
                return Results.NoContent();
            }
            catch (UnauthorizedAccessException) { return Results.Unauthorized(); }
            catch (KeyNotFoundException) { return Results.NotFound(); }
            catch (InvalidOperationException) { return Results.Conflict(); }
            catch (Exception exception) when (exception is ArgumentException or JsonException) { return Results.BadRequest(); }
        });
    }
}

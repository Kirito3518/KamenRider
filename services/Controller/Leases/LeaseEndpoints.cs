using KamenRaider.Contracts;
namespace KamenRaider.Controller.Leases;
public static class LeaseEndpoints {
    // These assertions are accepted only behind the loopback/service-credential middleware.
    // The Next server must construct them from its verified session, never forward browser headers.
    private static string Actor(HttpContext context) => context.Request.Headers["X-Authenticated-Actor-Id"].ToString();
    private static bool IsAdmin(HttpContext context) => context.Request.Headers["X-Authenticated-Actor-Role"] == "admin";
    public static void MapLeases(this WebApplication app) {
        app.MapPost("/internal/trial-leases",async (TrialLeaseRequest request,HttpContext context,LeaseService leases,CancellationToken ct)=> {
            if(string.IsNullOrWhiteSpace(Actor(context))||(!IsAdmin(context)&&Actor(context)!=request.UserId)) return Results.StatusCode(403);
            try { return Results.Ok(await leases.CreateTrialLeaseAsync(request.UserId,request.MachineId,request.DurationSeconds,request.IdempotencyKey,ct)); }
            catch(ArgumentException) { return Results.BadRequest(new {error="Invalid lease request"}); }
            catch(InvalidOperationException) { return Results.Conflict(new {error="Lease conflict"}); }
        });
        app.MapPost("/internal/leases/{id}/extend",async (string id,ExtendLeaseRequest request,HttpContext context,LeaseService leases,CancellationToken ct)=> {
            var actor=Actor(context);if(string.IsNullOrWhiteSpace(actor)) return Results.StatusCode(403);
            var lease=await leases.GetLeaseAsync(id,ct);if(lease is null) return Results.NotFound();
            if(!IsAdmin(context)&&lease.UserId!=actor) return Results.StatusCode(403);
            try { return Results.Ok(await leases.ExtendLeaseAsync(id,request.AdditionalSeconds,request.ExpectedRevision,request.IdempotencyKey,ct)); }
            catch(ArgumentException) { return Results.BadRequest(new {error="Invalid lease request"}); }
            catch(InvalidOperationException) { return Results.Conflict(new {error="Lease conflict"}); }
        });
        app.MapGet("/internal/users/{userId}/leases",async (string userId,HttpContext context,LeaseService leases,CancellationToken ct)=> {
            if(string.IsNullOrWhiteSpace(Actor(context))||Actor(context)!=userId) return Results.StatusCode(403);
            try { return Results.Ok(await leases.GetUserLeasesAsync(userId,ct)); }
            catch(ArgumentException) { return Results.BadRequest(); }
        });
    }
}

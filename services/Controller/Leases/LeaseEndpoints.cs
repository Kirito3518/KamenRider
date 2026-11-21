using KamenRaider.Contracts;
namespace KamenRaider.Controller.Leases;
public static class LeaseEndpoints {
    // These assertions are accepted only behind the loopback/service-credential middleware.
    // The Next server must construct them from its verified session, never forward browser headers.
    private static string Actor(HttpContext context) => context.Request.Headers["X-Authenticated-Actor-Id"].ToString();
    private static bool IsAdmin(HttpContext context) => context.Request.Headers["X-Authenticated-Actor-Role"] == "admin";
    public static void MapLeases(this WebApplication app) {
        app.MapGet("/internal/leases/{id}/connection",async(string id,HttpContext context,LeaseService leases,KamenRaider.Controller.Data.ControllerDatabase db,IServiceProvider services,CancellationToken ct)=>{
            var lease=await leases.GetLeaseAsync(id,ct);if(lease is null)return Results.NotFound();
            if(string.IsNullOrWhiteSpace(Actor(context))||lease.UserId!=Actor(context))return Results.StatusCode(403);
            var ip=services.GetService<KamenRaider.Controller.Network.PublicIpJob>();
            var machine=db.List().Single(x=>x.MachineId==lease.MachineId);
            var address=lease.State=="active"&&ip?.Address is string publicIp?$"{publicIp}:{machine.MoonlightPort}":null;
            return Results.Ok(new {address,stale=ip?.Stale??true});
        });
        // Only the authenticated owner may rent for themselves; administrators never rent on behalf
        // of a customer.
        app.MapPost("/internal/leases",async (CreateLeaseRequest request,HttpContext context,LeaseService leases,CancellationToken ct)=> {
            var actor=Actor(context);
            if(string.IsNullOrWhiteSpace(actor)||actor!=request.UserId) return Results.StatusCode(403);
            try { return Results.Ok(await leases.CreateLeaseAsync(request.UserId,request.MachineId,request.DurationSeconds,request.IdempotencyKey,ct)); }
            catch(ArgumentException) { return Results.BadRequest(new {error="Invalid lease request"}); }
            catch(InvalidOperationException) { return Results.Conflict(new {error="Lease conflict"}); }
        });
        // Administrative history is lease data, so it is never cached by intermediaries.
        app.MapGet("/internal/leases",async (HttpContext context,LeaseService leases,CancellationToken ct)=> {
            if(string.IsNullOrWhiteSpace(Actor(context))||!IsAdmin(context)) return Results.StatusCode(403);
            var take=200;
            if(context.Request.Query.TryGetValue("take",out var values)
                && (values.Count!=1||!int.TryParse(values[0],System.Globalization.NumberStyles.None,System.Globalization.CultureInfo.InvariantCulture,out take)||take is <1 or >500))
                return Results.BadRequest(new {error="Invalid take"});
            context.Response.Headers["Cache-Control"]="no-store";
            try { return Results.Ok(await leases.GetAllLeasesAsync(take,ct)); }
            catch(ArgumentException) { return Results.BadRequest(new {error="Invalid take"}); }
        });
        app.MapPost("/internal/leases/{id}/extend",async (string id,ExtendLeaseRequest request,HttpContext context,LeaseService leases,CancellationToken ct)=> {
            var actor=Actor(context);if(string.IsNullOrWhiteSpace(actor)) return Results.StatusCode(403);
            var lease=await leases.GetLeaseAsync(id,ct);if(lease is null) return Results.NotFound();
            if(!IsAdmin(context)&&lease.UserId!=actor) return Results.StatusCode(403);
            try { return Results.Ok(await leases.ExtendLeaseAsync(id,request.AdditionalSeconds,request.ExpectedRevision,request.IdempotencyKey,ct)); }
            catch(ArgumentException) { return Results.BadRequest(new {error="Invalid lease request"}); }
            catch(InvalidOperationException) { return Results.Conflict(new {error="Lease conflict"}); }
        });
        app.MapPost("/internal/leases/{id}/power",async (string id,LeasePowerRequest request,HttpContext context,LeaseService leases,CancellationToken ct)=> {
            var actor=Actor(context);if(string.IsNullOrWhiteSpace(actor)||request is null||!Guid.TryParse(request.RequestId,out _)) return Results.BadRequest(new {error="Invalid power request"});
            var lease=await leases.GetLeaseAsync(id,ct);if(lease is null) return Results.NotFound();
            if(lease.UserId!=actor) return Results.StatusCode(403);
            try { return Results.Ok(await leases.RequestPowerAsync(id,actor,request.RequestId,ct)); }
            catch(KeyNotFoundException) { return Results.NotFound(); }
            catch(UnauthorizedAccessException) { return Results.StatusCode(403); }
            catch(InvalidOperationException) { return Results.Conflict(new {error="Power request is not available"}); }
        });
        app.MapPost("/internal/leases/{id}/cancel",async (string id,LeaseCancelRequest request,HttpContext context,LeaseService leases,CancellationToken ct)=> {
            var actor=Actor(context);if(string.IsNullOrWhiteSpace(actor)||request is null||string.IsNullOrWhiteSpace(request.IdempotencyKey)) return Results.BadRequest(new {error="Invalid cancel request"});
            var lease=await leases.GetLeaseAsync(id,ct);if(lease is null) return Results.NotFound();
            if(lease.UserId!=actor) return Results.StatusCode(403);
            try { return Results.Ok(await leases.CancelUserLeaseAsync(id,actor,request.ExpectedRevision,request.IdempotencyKey,ct)); }
            catch(KeyNotFoundException) { return Results.NotFound(); }
            catch(UnauthorizedAccessException) { return Results.StatusCode(403); }
            catch(InvalidOperationException) { return Results.Conflict(new {error="Lease conflict"}); }
        });
        app.MapGet("/internal/users/{userId}/leases",async (string userId,HttpContext context,LeaseService leases,CancellationToken ct)=> {
            if(string.IsNullOrWhiteSpace(Actor(context))||Actor(context)!=userId) return Results.StatusCode(403);
            try { return Results.Ok(await leases.GetUserLeasesAsync(userId,ct)); }
            catch(ArgumentException) { return Results.BadRequest(); }
        });
    }
}

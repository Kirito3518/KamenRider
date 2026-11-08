using System.Net.Sockets;
using System.Net.WebSockets;
using KamenRaider.Contracts;
using KamenRaider.Controller.Data;

namespace KamenRaider.Controller.RemoteDesktop;

public sealed record AdminVncTargetProbeRequest(string MachineId, string TargetAddress);

public static class AdminVncEndpoints
{
    private const string TicketProtocolPrefix = "kamen-vnc-";
    private const int MaximumClientMessageBytes = 4 * 1024 * 1024;
    private static readonly TimeSpan MaximumSessionDuration = TimeSpan.FromHours(1);

    public static void MapAdminVnc(this WebApplication app)
    {
        app.MapPost("/internal/admin-vnc-tickets", (CreateAdminVncTicketRequest request, HttpContext context, ControllerDatabase database) =>
        {
            if (!string.Equals(context.Request.Headers["X-Authenticated-Actor-Role"].ToString(), "admin", StringComparison.Ordinal))
                return Results.StatusCode(StatusCodes.Status403Forbidden);
            var adminId = context.Request.Headers["X-Authenticated-Actor-Id"].ToString();
            try
            {
                return Results.Ok(database.CreateAdminVncTicket(request.MachineId, adminId, request.AllowedOrigin));
            }
            catch (UnauthorizedAccessException)
            {
                return Results.StatusCode(StatusCodes.Status409Conflict);
            }
            catch (ArgumentException)
            {
                return Results.BadRequest(new { error = "Invalid VNC session request" });
            }
        });

        app.MapPost("/internal/admin-vnc-target-probes", async (
            AdminVncTargetProbeRequest request,
            HttpContext context,
            ControllerDatabase database,
            IAdminVncTargetProbe probe,
            CancellationToken cancellationToken) =>
        {
            if (!string.Equals(context.Request.Headers["X-Authenticated-Actor-Role"].ToString(), "admin", StringComparison.Ordinal)
                || string.IsNullOrWhiteSpace(context.Request.Headers["X-Authenticated-Actor-Id"].ToString()))
                return Results.StatusCode(StatusCodes.Status403Forbidden);

            try
            {
                var address = database.ValidateAdminVncTargetProbe(request.MachineId, request.TargetAddress);
                var supported = await probe.SupportsReadOnlyGatewayAsync(address, cancellationToken);
                return Results.Ok(new { supported });
            }
            catch (UnauthorizedAccessException)
            {
                return Results.StatusCode(StatusCodes.Status409Conflict);
            }
            catch (ArgumentException)
            {
                return Results.BadRequest(new { error = "Invalid VNC target probe request" });
            }
        });

        app.MapGet("/admin/vnc", ProxyAsync);
    }

    private static async Task ProxyAsync(HttpContext context, ControllerDatabase database, ILoggerFactory loggerFactory)
    {
        var logger = loggerFactory.CreateLogger("AdminVncProxy");
        if (!context.WebSockets.IsWebSocketRequest)
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            return;
        }

        var protocols = context.WebSockets.WebSocketRequestedProtocols;
        if (!protocols.Contains("binary", StringComparer.Ordinal) || protocols.Count(x => x.StartsWith(TicketProtocolPrefix, StringComparison.Ordinal)) != 1)
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            return;
        }

        var ticketProtocol = protocols.Single(x => x.StartsWith(TicketProtocolPrefix, StringComparison.Ordinal));
        var ticket = ticketProtocol[TicketProtocolPrefix.Length..];
        AdminVncTarget target;
        try
        {
            target = database.ConsumeAdminVncTicket(ticket, context.Request.Headers.Origin.ToString());
        }
        catch (ArgumentException)
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            return;
        }
        catch (UnauthorizedAccessException)
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            return;
        }

        try
        {
            using var client = new TcpClient(target.Address.AddressFamily);
            using var connectTimeout = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted);
            connectTimeout.CancelAfter(TimeSpan.FromSeconds(5));
            await client.ConnectAsync(target.Address, target.Port, connectTimeout.Token);

            using var sessionTimeout = new CancellationTokenSource(MaximumSessionDuration);
            using var sessionCancellation = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted, sessionTimeout.Token);
            using var webSocket = await context.WebSockets.AcceptWebSocketAsync("binary");
            await using var vncStream = client.GetStream();
            var readOnlyProtocol = new ReadOnlyVncProtocol();
            logger.LogInformation("Admin VNC session started for machine {MachineId} by admin {AdminUserId}", target.MachineId, target.AdminUserId);

            var clientToVnc = CopyWebSocketToVncAsync(webSocket, vncStream, readOnlyProtocol, sessionCancellation.Token);
            var vncToClient = CopyVncToWebSocketAsync(vncStream, webSocket, readOnlyProtocol, sessionCancellation.Token);
            await Task.WhenAny(clientToVnc, vncToClient);
            await sessionCancellation.CancelAsync();
            try
            {
                await Task.WhenAll(clientToVnc, vncToClient);
            }
            catch (OperationCanceledException) when (sessionCancellation.IsCancellationRequested)
            {
            }
            catch (WebSocketException)
            {
            }
            if (webSocket.State == WebSocketState.Open)
                await webSocket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "Session ended", CancellationToken.None);
        }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
        {
        }
        catch (Exception exception) when (exception is IOException or SocketException or WebSocketException or OperationCanceledException or InvalidDataException)
        {
            logger.LogInformation("Admin VNC session ended for machine {MachineId} ({ErrorType})", target.MachineId, exception.GetType().Name);
            if (!context.Response.HasStarted)
                context.Response.StatusCode = StatusCodes.Status502BadGateway;
        }
        finally
        {
            database.FinishAdminVncSession(target.SessionId);
        }
    }

    private static async Task CopyWebSocketToVncAsync(WebSocket webSocket, Stream vncStream, ReadOnlyVncProtocol readOnlyProtocol, CancellationToken cancellationToken)
    {
        var buffer = new byte[16 * 1024];
        while (!cancellationToken.IsCancellationRequested && webSocket.State == WebSocketState.Open)
        {
            using var message = new MemoryStream();
            WebSocketReceiveResult result;
            do
            {
                result = await webSocket.ReceiveAsync(new ArraySegment<byte>(buffer), cancellationToken);
                if (result.MessageType == WebSocketMessageType.Close) return;
                if (result.MessageType != WebSocketMessageType.Binary)
                    throw new WebSocketException(WebSocketError.InvalidMessageType);
                if (message.Length + result.Count > MaximumClientMessageBytes)
                    throw new WebSocketException(WebSocketError.HeaderError, "VNC client message exceeds the configured limit");
                message.Write(buffer, 0, result.Count);
            } while (!result.EndOfMessage);

            var allowed = readOnlyProtocol.FilterClientMessage(message.GetBuffer().AsSpan(0, checked((int)message.Length)));
            await vncStream.WriteAsync(allowed, cancellationToken);
            await vncStream.FlushAsync(cancellationToken);
        }
    }

    private static async Task CopyVncToWebSocketAsync(Stream vncStream, WebSocket webSocket, ReadOnlyVncProtocol readOnlyProtocol, CancellationToken cancellationToken)
    {
        var buffer = new byte[64 * 1024];
        while (!cancellationToken.IsCancellationRequested)
        {
            var read = await vncStream.ReadAsync(buffer, cancellationToken);
            if (read == 0) return;
            var safe = readOnlyProtocol.FilterServerBytes(buffer.AsSpan(0, read));
            if (safe.Length == 0) continue;
            await webSocket.SendAsync(safe, WebSocketMessageType.Binary, true, cancellationToken);
        }
    }
}

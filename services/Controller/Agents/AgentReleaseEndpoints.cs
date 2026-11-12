using KamenRaider.Controller.Data;
using KamenRaider.Controller.Machines;
using KamenRaider.Controller.Security;
using KamenRaider.Contracts;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace KamenRaider.Controller.Agents;

public static class AgentReleaseEndpoints
{
    private const long MaximumMultipartRequestBytes = AgentReleaseStore.MaximumCompressedBytes + 128 * 1024;
    private const int MaximumSourceRequestBytes = 64 * 1024;
    private static readonly JsonSerializerOptions SignedAgentJsonOptions = new(JsonSerializerDefaults.Web);

    public static void MapAgentReleases(this WebApplication app)
    {
        app.MapGet("/internal/agent-releases", (HttpContext context, ControllerDatabase database) =>
            IsAdmin(context) ? Results.Ok(database.ListAgentReleases()) : Results.StatusCode(StatusCodes.Status403Forbidden));

        app.MapPost("/internal/agent-releases", PublishAsync);
        app.MapPost("/agent/machines/{machineId}/agent-updates/{operationId}/start", StartUpdateAsync);
        app.MapPost("/agent/machines/{machineId}/agent-updates/{operationId}/source-challenge", IssueSourceChallengeAsync);
        app.MapPost("/agent/machines/{machineId}/agent-updates/{operationId}/source-binding", BindSourceAsync);
        app.MapPost("/agent/machines/{machineId}/agent-updates/{operationId}/status", ReportUpdateStatusAsync);
        app.MapGet("/agent/machines/{machineId}/agent-updates/{operationId}/package", DownloadUpdatePackageAsync);
    }

    private static IResult StartUpdateAsync(string machineId, string operationId, AgentUpdateTicketRequest? request, HttpContext context, ControllerDatabase database)
    {
        context.Response.Headers.CacheControl = "no-store";
        if (request is null || string.IsNullOrWhiteSpace(request.RequestId)) return Results.BadRequest();
        return ExecuteTicketed(() => database.StartAgentUpdate(machineId, operationId, request.Ticket, request.ReleaseVersion, request.TargetVersion, request.TargetPackageSha256, request.RequestId));
    }

    private static async Task<IResult> IssueSourceChallengeAsync(string machineId, string operationId, HttpContext context,
        ControllerDatabase database, AgentAuthentication authentication, CancellationToken cancellationToken)
    {
        context.Response.Headers.CacheControl = "no-store";
        context.Response.Headers.Pragma = "no-cache";
        var signedBody = await ReadSignedBodyAsync(context, database, authentication, $"/agent/machines/{machineId}/agent-updates/{operationId}/source-challenge", cancellationToken);
        if (signedBody.StatusCode != StatusCodes.Status200OK) return Results.StatusCode(signedBody.StatusCode);
        try
        {
            var body = new UTF8Encoding(false, true).GetString(signedBody.Body!);
            var request = JsonSerializer.Deserialize<AgentUpdateSourceChallengeRequest>(body, SignedAgentJsonOptions);
            if (request is null) return Results.BadRequest();
            return Results.Ok(database.IssueAgentUpdateSourceChallenge(signedBody.Verified!, operationId, request));
        }
        catch (UnauthorizedAccessException) { return Results.Unauthorized(); }
        catch (AgentUpdateSourceChallengeRenewalRequiredException) { return Results.Conflict(new { code = "sourceChallengeRenewalRequired" }); }
        catch (InvalidOperationException) { return Results.Conflict(); }
        catch (Exception exception) when (exception is ArgumentException or JsonException or DecoderFallbackException) { return Results.BadRequest(); }
        catch (Exception exception) when (exception is IOException or Microsoft.Data.Sqlite.SqliteException) { return Results.StatusCode(StatusCodes.Status503ServiceUnavailable); }
    }

    private static async Task<IResult> BindSourceAsync(string machineId, string operationId, HttpContext context,
        ControllerDatabase database, AgentAuthentication authentication, CancellationToken cancellationToken)
    {
        context.Response.Headers.CacheControl = "no-store";
        context.Response.Headers.Pragma = "no-cache";
        var signedBody = await ReadSignedBodyAsync(context, database, authentication, $"/agent/machines/{machineId}/agent-updates/{operationId}/source-binding", cancellationToken);
        if (signedBody.StatusCode != StatusCodes.Status200OK) return Results.StatusCode(signedBody.StatusCode);
        try
        {
            var body = new UTF8Encoding(false, true).GetString(signedBody.Body!);
            var request = JsonSerializer.Deserialize<AgentUpdateSourceBindingRequest>(body, SignedAgentJsonOptions);
            if (request is null) return Results.BadRequest();
            return Results.Ok(database.BindAgentUpdateSource(signedBody.Verified!, operationId, request));
        }
        catch (UnauthorizedAccessException) { return Results.Unauthorized(); }
        catch (AgentUpdateSourceChallengeRenewalRequiredException) { return Results.Conflict(new { code = "sourceChallengeRenewalRequired" }); }
        catch (InvalidOperationException) { return Results.Conflict(); }
        catch (KeyNotFoundException) { return Results.NotFound(); }
        catch (Exception exception) when (exception is ArgumentException or JsonException or DecoderFallbackException) { return Results.BadRequest(); }
        catch (Exception exception) when (exception is IOException or Microsoft.Data.Sqlite.SqliteException) { return Results.StatusCode(StatusCodes.Status503ServiceUnavailable); }
    }

    private static async Task<(int StatusCode, byte[]? Body, VerifiedAgentRequest? Verified)> ReadSignedBodyAsync(
        HttpContext context, ControllerDatabase database, AgentAuthentication authentication, string path,
        CancellationToken cancellationToken)
    {
        context.Request.EnableBuffering();
        if (!string.Equals(context.Request.Path.Value, path, StringComparison.Ordinal) || context.Request.QueryString.HasValue)
            return (StatusCodes.Status401Unauthorized, null, null);
        if (context.Request.ContentLength is > MaximumSourceRequestBytes)
            return (StatusCodes.Status413PayloadTooLarge, null, null);
        using var output = new MemoryStream();
        var buffer = new byte[4096];
        while (true)
        {
            var count = await context.Request.Body.ReadAsync(buffer.AsMemory(), cancellationToken);
            if (count == 0) break;
            if (output.Length + count > MaximumSourceRequestBytes)
                return (StatusCodes.Status413PayloadTooLarge, null, null);
            output.Write(buffer, 0, count);
        }
        var bytes = output.ToArray();
        var machineId = context.Request.Headers["X-Agent-Id"].ToString();
        var publicKey = database.PublicKey(machineId);
        if (publicKey is null) return (StatusCodes.Status401Unauthorized, null, null);
        var verified = authentication.VerifySignature(machineId, publicKey, "POST", path,
            context.Request.Headers["X-Agent-Timestamp"].ToString(), context.Request.Headers["X-Agent-Nonce"].ToString(),
            bytes, context.Request.Headers["X-Agent-Signature"].ToString());
        return verified is null
            ? (StatusCodes.Status401Unauthorized, null, null)
            : (StatusCodes.Status200OK, bytes, verified);
    }

    private static IResult ReportUpdateStatusAsync(string machineId, string operationId, AgentUpdateStatusReportRequest? request, HttpContext context, ControllerDatabase database)
    {
        context.Response.Headers.CacheControl = "no-store";
        if (request is null) return Results.BadRequest();
        return ExecuteTicketed(() => database.ReportAgentUpdateStatus(machineId, operationId, request.Ticket,
            request.ReleaseVersion, request.TargetVersion, request.TargetPackageSha256, request.Status));
    }

    private static async Task<IResult> DownloadUpdatePackageAsync(string machineId, string operationId, HttpContext context, ControllerDatabase database, AgentReleaseStore store, CancellationToken cancellationToken)
    {
        context.Response.Headers.CacheControl = "no-store";
        context.Response.Headers.Pragma = "no-cache";
        context.Response.Headers["X-Content-Type-Options"] = "nosniff";
        var ticket = context.Request.Headers["X-Agent-Update-Ticket"].ToString();
        var releaseVersion = context.Request.Headers["X-Agent-Update-Release-Version"].ToString();
        var targetVersion = context.Request.Headers["X-Agent-Update-Target-Version"].ToString();
        var packageHash = context.Request.Headers["X-Agent-Update-Package-SHA256"].ToString();

        try
        {
            var operation = database.ConsumeAgentUpdatePackageDownload(machineId, operationId, ticket, releaseVersion, targetVersion, packageHash);
            using var package = store.OpenReadByHash(operation.TargetPackageSha256);
            if (!string.Equals(package.Sha256, operation.TargetPackageSha256, StringComparison.OrdinalIgnoreCase))
                return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);

            context.Response.StatusCode = StatusCodes.Status200OK;
            context.Response.ContentType = "application/zip";
            context.Response.ContentLength = package.Length;
            context.Response.Headers.ContentDisposition = "attachment; filename=\"AgentPackage.zip\"";
            await package.Content.CopyToAsync(context.Response.Body, 64 * 1024, cancellationToken);
            return Results.Empty;
        }
        catch (UnauthorizedAccessException)
        {
            return Results.StatusCode(StatusCodes.Status401Unauthorized);
        }
        catch (KeyNotFoundException)
        {
            return Results.StatusCode(StatusCodes.Status404NotFound);
        }
        catch (InvalidOperationException)
        {
            return Results.StatusCode(StatusCodes.Status409Conflict);
        }
        catch (ArgumentException)
        {
            return Results.StatusCode(StatusCodes.Status400BadRequest);
        }
        catch (FileNotFoundException)
        {
            return Results.StatusCode(StatusCodes.Status404NotFound);
        }
        catch (IOException)
        {
            return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
        }
        catch (InvalidDataException)
        {
            return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
        }
    }

    private static IResult ExecuteTicketed<T>(Func<T> action)
    {
        try { return Results.Ok(action()); }
        catch (UnauthorizedAccessException) { return Results.StatusCode(StatusCodes.Status401Unauthorized); }
        catch (KeyNotFoundException) { return Results.StatusCode(StatusCodes.Status404NotFound); }
        catch (ArgumentException) { return Results.BadRequest(); }
        catch (InvalidOperationException) { return Results.Conflict(); }
    }

    private static async Task<IResult> PublishAsync(HttpContext context, ControllerDatabase database, AgentReleaseStore store, CancellationToken cancellationToken)
    {
        if (!IsAdmin(context)) return Results.StatusCode(StatusCodes.Status403Forbidden);
        var actorId = context.Request.Headers["X-Authenticated-Actor-Id"].ToString();
        if (string.IsNullOrWhiteSpace(actorId)) return Results.StatusCode(StatusCodes.Status403Forbidden);
        if (database.IsRestoreQuarantined())
            return Results.Conflict(new { error = "Agent release publication is unavailable while the Controller is globally quarantined after restore" });
        if (context.Request.ContentLength is > MaximumMultipartRequestBytes)
            return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);
        if (context.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } sizeFeature)
            sizeFeature.MaxRequestBodySize = MaximumMultipartRequestBytes;
        if (!MediaTypeHeaderValue.TryParse(context.Request.ContentType, out var contentType)
            || !string.Equals(contentType.MediaType.Value, "multipart/form-data", StringComparison.OrdinalIgnoreCase))
            return Results.BadRequest(new { error = "Expected multipart/form-data" });
        var boundary = HeaderUtilities.RemoveQuotes(contentType.Boundary).Value;
        if (string.IsNullOrWhiteSpace(boundary) || boundary.Length > 128)
            return Results.BadRequest(new { error = "Invalid multipart boundary" });

        AgentReleaseStore.StagedAgentPackage? staged = null;
        try
        {
            var reader = new MultipartReader(boundary, context.Request.Body)
            {
                HeadersCountLimit = 16,
                HeadersLengthLimit = 16 * 1024,
                BodyLengthLimit = MaximumMultipartRequestBytes,
            };
            string? version = null;
            string? channel = null;
            string? releaseNotes = null;
            var sawPackage = false;
            MultipartSection? section;
            while ((section = await reader.ReadNextSectionAsync(cancellationToken)) is not null)
            {
                if (!ContentDispositionHeaderValue.TryParse(section.ContentDisposition, out var disposition)
                    || !disposition.DispositionType.Equals("form-data", StringComparison.OrdinalIgnoreCase))
                    return Results.BadRequest(new { error = "Invalid multipart section" });
                var fieldName = HeaderUtilities.RemoveQuotes(disposition.Name).Value;
                var fileName = HeaderUtilities.RemoveQuotes(disposition.FileName).Value;
                if (string.Equals(fieldName, "package", StringComparison.Ordinal))
                {
                    if (fileName is null || !fileName.Equals("AgentPackage.zip", StringComparison.OrdinalIgnoreCase)
                        || version is null || channel is null || releaseNotes is null || sawPackage)
                        return Results.BadRequest(new { error = "Package must be AgentPackage.zip and follow the release fields" });
                    if (section.ContentType is { } packageContentType
                        && (!MediaTypeHeaderValue.TryParse(packageContentType, out var parsedPackageType)
                            || !string.Equals(parsedPackageType.MediaType.Value, "application/zip", StringComparison.OrdinalIgnoreCase)))
                        return Results.BadRequest(new { error = "AgentPackage.zip must use application/zip" });
                    staged = await store.StageAsync(section.Body, cancellationToken);
                    sawPackage = true;
                    if (await reader.ReadNextSectionAsync(cancellationToken) is not null)
                        return Results.BadRequest(new { error = "AgentPackage.zip must be the last multipart field" });
                    break;
                }

                if (sawPackage || fileName is not null)
                    return Results.BadRequest(new { error = "Unexpected multipart field" });
                var field = await ReadFieldAsync(section.Body, fieldName switch
                {
                    "version" => 256,
                    "channel" => 32,
                    "releaseNotes" => AgentReleaseStore.MaximumReleaseNotesLength * 4,
                    _ => 0,
                }, cancellationToken);
                if (field is null) return Results.BadRequest(new { error = "Unexpected or oversized release field" });
                switch (fieldName)
                {
                    case "version" when version is null: version = field; break;
                    case "channel" when channel is null: channel = field; break;
                    case "releaseNotes" when releaseNotes is null: releaseNotes = field; break;
                    default: return Results.BadRequest(new { error = "Duplicate or unexpected release field" });
                }
            }

            if (!sawPackage || staged is null || version is null || channel is null || releaseNotes is null)
                return Results.BadRequest(new { error = "Release fields and AgentPackage.zip are required" });
            return Results.Ok(store.PublishStaged(database, staged, version, channel, releaseNotes, actorId));
        }
        catch (AgentReleaseTooLargeException)
        {
            return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);
        }
        catch (AgentReleaseConflictException exception)
        {
            return Results.Conflict(new { error = exception.Message });
        }
        catch (AgentReleaseValidationException exception)
        {
            return Results.BadRequest(new { error = exception.Message });
        }
        catch (InvalidDataException)
        {
            return Results.BadRequest(new { error = "Invalid multipart request or ZIP archive" });
        }
        catch (DecoderFallbackException)
        {
            return Results.BadRequest(new { error = "Release fields must use valid UTF-8" });
        }
        finally
        {
            staged?.Dispose();
        }
    }

    private static async Task<string?> ReadFieldAsync(Stream content, int maximumBytes, CancellationToken cancellationToken)
    {
        if (maximumBytes <= 0) return null;
        using var output = new MemoryStream();
        var buffer = new byte[4096];
        while (true)
        {
            var read = await content.ReadAsync(buffer.AsMemory(), cancellationToken);
            if (read == 0) break;
            if (output.Length + read > maximumBytes) return null;
            output.Write(buffer, 0, read);
        }
        return new UTF8Encoding(false, true).GetString(output.ToArray());
    }

    private static bool IsAdmin(HttpContext context) => !string.IsNullOrWhiteSpace(context.Request.Headers["X-Authenticated-Actor-Id"])
        && context.Request.Headers["X-Authenticated-Actor-Role"] == "admin";
}

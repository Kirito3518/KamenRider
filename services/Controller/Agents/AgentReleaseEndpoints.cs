using KamenRaider.Controller.Data;
using KamenRaider.Controller.Machines;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Net.Http.Headers;
using System.Text;

namespace KamenRaider.Controller.Agents;

public static class AgentReleaseEndpoints
{
    private const long MaximumMultipartRequestBytes = AgentReleaseStore.MaximumCompressedBytes + 128 * 1024;

    public static void MapAgentReleases(this WebApplication app)
    {
        app.MapGet("/internal/agent-releases", (HttpContext context, ControllerDatabase database) =>
            IsAdmin(context) ? Results.Ok(database.ListAgentReleases()) : Results.StatusCode(StatusCodes.Status403Forbidden));

        app.MapPost("/internal/agent-releases", PublishAsync);
    }

    private static async Task<IResult> PublishAsync(HttpContext context, ControllerDatabase database, AgentReleaseStore store, CancellationToken cancellationToken)
    {
        if (!IsAdmin(context)) return Results.StatusCode(StatusCodes.Status403Forbidden);
        var actorId = context.Request.Headers["X-Authenticated-Actor-Id"].ToString();
        if (string.IsNullOrWhiteSpace(actorId)) return Results.StatusCode(StatusCodes.Status403Forbidden);
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

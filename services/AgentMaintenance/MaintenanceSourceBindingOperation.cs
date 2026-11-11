using System.Net;
using System.Net.Http.Headers;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using KamenRaider.Contracts;

namespace KamenRaider.AgentMaintenance;

public sealed record MaintenanceSourceBindingParameters(
    string Ticket,
    string ReleaseVersion,
    string HelperBuildSha256,
    string HelperRuntime,
    int HelperProtocol);

public sealed record MaintenanceSourceBindingResult(
    string SourceVersion,
    string InventoryDigest,
    string BackupDigest,
    AgentUpdateOperationDto ControllerOperation);

public sealed class MaintenanceSourceChallengeRenewalRequiredException : InvalidOperationException
{
    public MaintenanceSourceChallengeRenewalRequiredException()
        : base("The Controller rejected an unconsumed source challenge that expired or belongs to a prior runtime.") { }
}

public interface IMaintenanceSourceBindingClient
{
    Task<AgentUpdateSourceChallengeResponse> IssueChallengeAsync(
        string controllerOrigin,
        string machineId,
        string operationId,
        AgentUpdateSourceChallengeRequest request,
        TrustedIdentity identity,
        CancellationToken cancellationToken);

    Task<AgentUpdateOperationDto> BindSourceAsync(
        string controllerOrigin,
        string machineId,
        string operationId,
        AgentUpdateSourceBindingRequest request,
        TrustedIdentity identity,
        CancellationToken cancellationToken);
}

public interface IMaintenanceBootIdentityProvider
{
    string ReadBootId();
}

[SupportedOSPlatform("windows")]
public sealed class WindowsMaintenanceBootIdentityProvider : IMaintenanceBootIdentityProvider
{
    public string ReadBootId()
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("Native Windows boot identity is available only on Windows.");
        var information = new BootEnvironment();
        var status = NtQuerySystemInformation(90, ref information, Marshal.SizeOf<BootEnvironment>(), out _);
        if (status != 0 || information.BootIdentifier == Guid.Empty)
            throw new IOException("The native Windows boot identity is unavailable.");
        return information.BootIdentifier.ToString("N");
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BootEnvironment
    {
        public Guid BootIdentifier;
        public int FirmwareType;
        public ulong BootFlags;
    }

    [DllImport("ntdll.dll")]
    private static extern int NtQuerySystemInformation(int informationClass, ref BootEnvironment information,
        int informationLength, out int returnLength);
}

public sealed class HttpMaintenanceSourceBindingClient : IMaintenanceSourceBindingClient
{
    private const int MaximumResponseBytes = 64 * 1024;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { MaxDepth = 8 };
    private readonly HttpClient httpClient;
    private readonly TimeProvider timeProvider;

    public HttpMaintenanceSourceBindingClient(HttpClient httpClient, TimeProvider? timeProvider = null)
    {
        this.httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        this.timeProvider = timeProvider ?? TimeProvider.System;
    }

    public static HttpMaintenanceSourceBindingClient CreateDefault(TimeProvider? timeProvider = null)
    {
        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            ConnectTimeout = TimeSpan.FromSeconds(15),
            PooledConnectionLifetime = TimeSpan.FromMinutes(5)
        };
        return new(new HttpClient(handler, disposeHandler: true) { Timeout = TimeSpan.FromSeconds(30) }, timeProvider);
    }

    public Task<AgentUpdateSourceChallengeResponse> IssueChallengeAsync(
        string controllerOrigin,
        string machineId,
        string operationId,
        AgentUpdateSourceChallengeRequest request,
        TrustedIdentity identity,
        CancellationToken cancellationToken)
    {
        var path = $"/agent/machines/{EscapeIdentifier(machineId)}/agent-updates/{EscapeIdentifier(operationId)}/source-challenge";
        return PostAsync<AgentUpdateSourceChallengeRequest, AgentUpdateSourceChallengeResponse>(
            controllerOrigin, path, machineId, request, identity, cancellationToken);
    }

    public Task<AgentUpdateOperationDto> BindSourceAsync(
        string controllerOrigin,
        string machineId,
        string operationId,
        AgentUpdateSourceBindingRequest request,
        TrustedIdentity identity,
        CancellationToken cancellationToken)
    {
        var path = $"/agent/machines/{EscapeIdentifier(machineId)}/agent-updates/{EscapeIdentifier(operationId)}/source-binding";
        return PostAsync<AgentUpdateSourceBindingRequest, AgentUpdateOperationDto>(
            controllerOrigin, path, machineId, request, identity, cancellationToken);
    }

    private async Task<TResponse> PostAsync<TRequest, TResponse>(
        string controllerOrigin,
        string path,
        string machineId,
        TRequest request,
        TrustedIdentity identity,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(identity);
        if (!string.Equals(machineId, identity.MachineId, StringComparison.Ordinal))
            throw new InvalidOperationException("The signed maintenance request machine ID does not match the existing enrollment identity.");
        var origin = ValidateOrigin(controllerOrigin);
        var uri = new Uri(origin, path);
        var body = JsonSerializer.SerializeToUtf8Bytes(request, JsonOptions);
        var seconds = timeProvider.GetUtcNow().ToUnixTimeSeconds();
        var nonce = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
        using var message = new HttpRequestMessage(HttpMethod.Post, uri)
        {
            Content = new ByteArrayContent(body)
        };
        message.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        message.Headers.TryAddWithoutValidation("X-Agent-Id", machineId);
        message.Headers.TryAddWithoutValidation("X-Agent-Timestamp", seconds.ToString(System.Globalization.CultureInfo.InvariantCulture));
        message.Headers.TryAddWithoutValidation("X-Agent-Nonce", nonce);
        message.Headers.TryAddWithoutValidation("X-Agent-Signature", identity.Sign("POST", path, seconds, nonce,
            Encoding.UTF8.GetString(body)));

        using var response = await httpClient.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);
        if (response.RequestMessage?.RequestUri is { } finalUri && finalUri != uri)
            throw new InvalidOperationException("The Controller redirected a signed maintenance request.");
        if (!response.IsSuccessStatusCode)
        {
            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                throw new UnauthorizedAccessException("The Controller rejected the signed maintenance request.");
            if (response.StatusCode == HttpStatusCode.Conflict)
            {
                var errorBytes = await ReadBoundedAsync(response.Content, cancellationToken).ConfigureAwait(false);
                if (IsChallengeRenewalRequired(errorBytes))
                    throw new MaintenanceSourceChallengeRenewalRequiredException();
                throw new InvalidOperationException("The Controller rejected the maintenance state transition.");
            }
            if (response.StatusCode == HttpStatusCode.RequestEntityTooLarge)
                throw new InvalidDataException("The Controller returned an oversized maintenance response.");
            throw new IOException($"The Controller maintenance endpoint returned HTTP {(int)response.StatusCode}.");
        }

        var responseBytes = await ReadBoundedAsync(response.Content, cancellationToken).ConfigureAwait(false);
        try
        {
            return JsonSerializer.Deserialize<TResponse>(responseBytes, JsonOptions)
                ?? throw new InvalidDataException("The Controller maintenance response was empty.");
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("The Controller maintenance response was malformed.", exception);
        }
    }

    private static async Task<byte[]> ReadBoundedAsync(HttpContent content, CancellationToken cancellationToken)
    {
        if (content.Headers.ContentLength is > MaximumResponseBytes)
            throw new InvalidDataException("The Controller maintenance response exceeds its size limit.");
        await using var input = await content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var output = new MemoryStream();
        var buffer = new byte[4096];
        while (true)
        {
            var read = await input.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false);
            if (read == 0) break;
            if (output.Length + read > MaximumResponseBytes)
                throw new InvalidDataException("The Controller maintenance response exceeds its size limit.");
            output.Write(buffer, 0, read);
        }
        return output.ToArray();
    }

    private static Uri ValidateOrigin(string value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var origin) || origin.Scheme != Uri.UriSchemeHttps ||
            !string.IsNullOrEmpty(origin.UserInfo) || !string.IsNullOrEmpty(origin.Query) ||
            !string.IsNullOrEmpty(origin.Fragment) || origin.AbsolutePath != "/" ||
            !string.Equals(origin.GetLeftPart(UriPartial.Authority) + "/", value, StringComparison.Ordinal))
            throw new InvalidDataException("The maintenance Controller origin is not a canonical HTTPS origin.");
        return origin;
    }

    private static bool IsChallengeRenewalRequired(byte[] responseBytes)
    {
        try
        {
            using var document = JsonDocument.Parse(responseBytes, new JsonDocumentOptions { MaxDepth = 4 });
            return document.RootElement.ValueKind == JsonValueKind.Object &&
                document.RootElement.TryGetProperty("code", out var code) &&
                code.ValueKind == JsonValueKind.String &&
                code.GetString() == "sourceChallengeRenewalRequired";
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static string EscapeIdentifier(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 128 ||
            value.Any(character => !char.IsAsciiLetterOrDigit(character) && character is not ('.' or '_' or '-')))
            throw new InvalidDataException("The signed maintenance route contains an invalid identifier.");
        return Uri.EscapeDataString(value);
    }
}

public sealed class MaintenanceSourceBindingOperation(
    MaintenanceBackupOperation backupOperation,
    MaintenanceJournal journal,
    IMaintenanceSourceBindingClient client,
    TrustedIdentity identity,
    IMaintenanceBootIdentityProvider bootIdentity,
    TimeProvider? timeProvider = null)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private const int MaximumRenewalsPerInvocation = 4;
    private readonly TimeProvider clock = timeProvider ?? TimeProvider.System;

    public async Task<MaintenanceSourceBindingResult> BindAsync(
        MaintenanceSourceBindingParameters parameters,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        ArgumentNullException.ThrowIfNull(backupOperation);
        ArgumentNullException.ThrowIfNull(journal);
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(identity);
        ArgumentNullException.ThrowIfNull(bootIdentity);
        ValidateParameters(parameters);

        for (var renewal = 0; ; renewal++)
        {
            var snapshot = journal.Read()
                ?? throw new InvalidOperationException("The maintenance journal is missing; source binding remains held.");
            if (snapshot.Operations.Count != 1 || snapshot.HeadOperationId != snapshot.Operations[0].Binding.OperationId)
                throw new InvalidOperationException("The source binding journal head is invalid.");
            var operation = snapshot.Operations[0];
            if (operation.Phase is not ("servicesQuiesced" or "backupPublished" or "sourceBound"))
                throw new InvalidOperationException("Source binding requires the quiesced-service journal phase or a later recoverable phase.");
            if (!string.Equals(parameters.HelperRuntime, operation.Binding.TargetRuntime, StringComparison.Ordinal))
                throw new InvalidDataException("The native helper architecture does not match the immutable target runtime.");

            MaintenanceBackupResult backup;
            InstalledAgentSourceSnapshot? source = null;
            MaintenanceSourceBindingIntent? intent = operation.SourceBindingIntent;
            if (operation.Phase is "servicesQuiesced" or "backupPublished")
            {
                var backupResult = backupOperation.Publish(operation.Binding.OperationId);
                backup = backupResult.Backup;
                source = backupResult.Source;
                if (intent is not null &&
                    (!string.Equals(intent.Evidence.SourceVersion, source.Version, StringComparison.Ordinal) ||
                     !FixedHexEquals(intent.Evidence.InventoryDigest, backup.InventoryDigest)))
                    throw new InvalidOperationException("The installed source no longer matches the immutable source-binding intent.");
                if (intent is null)
                {
                    var attemptNumber = NextAttemptNumber(operation);
                    try
                    {
                        intent = await CreateAndJournalIntentAsync(operation.Binding, source, backup, parameters,
                            attemptNumber, cancellationToken).ConfigureAwait(false);
                    }
                    catch (MaintenanceSourceChallengeRenewalRequiredException)
                    {
                        var pending = journal.Read()?.Operations.Single().PendingSourceChallengeAttempt
                            ?? throw new InvalidDataException("The rejected source challenge has no matching durable journal attempt.");
                        _ = journal.RejectPendingSourceChallengeAttempt(operation.Binding.OperationId, pending);
                        if (renewal >= MaximumRenewalsPerInvocation) throw;
                        continue;
                    }
                }
            }
            else
            {
                if (intent is null || operation.SourceVersion != intent.Evidence.SourceVersion ||
                    operation.SourceInventoryDigest != intent.Evidence.InventoryDigest)
                    throw new InvalidDataException("The sourceBound journal is missing its exact signed binding intent.");
                backup = backupOperation.VerifyPublishedBackup(operation.Binding.OperationId);
            }

            if (!string.Equals(backup.BackupOwnerOperationId, operation.Binding.OperationId, StringComparison.Ordinal) ||
                !FixedHexEquals(backup.BackupDigest, intent.Evidence.BackupDigest) ||
                !FixedHexEquals(backup.InventoryDigest, intent.Evidence.InventoryDigest))
                throw new InvalidDataException("The published backup no longer matches the signed source-binding intent.");

            var bindingRequest = BuildBindingRequest(operation.Binding, parameters, intent);
            var bodyDigest = ComputeBodyDigest(bindingRequest);
            if (!FixedHexEquals(bodyDigest, intent.BindingBodyDigest))
                throw new InvalidOperationException("The signed source-binding retry does not match its journaled request digest.");

            AgentUpdateOperationDto response;
            try
            {
                response = await client.BindSourceAsync(operation.Binding.ControllerOrigin, identity.MachineId,
                    operation.Binding.OperationId, bindingRequest, identity, cancellationToken).ConfigureAwait(false);
            }
            catch (MaintenanceSourceChallengeRenewalRequiredException) when (
                journal.Read()?.Operations.SingleOrDefault()?.Phase == "backupPublished")
            {
                _ = journal.RejectSourceBindingIntent(operation.Binding.OperationId, intent.AttemptNumber,
                    intent.SourceRequestId, intent.BindingBodyDigest);
                if (renewal >= MaximumRenewalsPerInvocation) throw;
                continue;
            }
            ValidateAcknowledgement(operation.Binding, identity.MachineId, bindingRequest, bodyDigest, response);
            _ = journal.Advance(operation.Binding.OperationId, "sourceBound",
                new MaintenanceJournalEvidence(SourceVersion: intent.Evidence.SourceVersion,
                    SourceInventoryDigest: intent.Evidence.InventoryDigest));
            return new(intent.Evidence.SourceVersion, intent.Evidence.InventoryDigest, intent.Evidence.BackupDigest, response);
        }
    }

    private async Task<MaintenanceSourceBindingIntent> CreateAndJournalIntentAsync(
        MaintenanceOperationBinding binding,
        InstalledAgentSourceSnapshot source,
        MaintenanceBackupResult backup,
        MaintenanceSourceBindingParameters parameters,
        int attemptNumber,
        CancellationToken cancellationToken)
    {
        var challengeRequestId = StableRequestId("source-challenge", binding.OperationId, attemptNumber);
        var challengeRequest = new AgentUpdateSourceChallengeRequest(parameters.Ticket, parameters.ReleaseVersion,
            binding.TargetVersion, binding.TargetSha256, challengeRequestId, parameters.HelperBuildSha256,
            parameters.HelperRuntime, parameters.HelperProtocol);
        var challengeRequestBodyDigest = ComputeBodyDigest(challengeRequest);
        _ = journal.RecordSourceChallengeAttempt(binding.OperationId,
            new MaintenanceSourceChallengeAttempt(attemptNumber, challengeRequestId, challengeRequestBodyDigest));
        var challenge = await client.IssueChallengeAsync(binding.ControllerOrigin, identity.MachineId,
            binding.OperationId, challengeRequest, identity, cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(challenge.ChallengeId) || string.IsNullOrWhiteSpace(challenge.Challenge) ||
            string.IsNullOrWhiteSpace(challenge.RuntimeEpoch) || challenge.ExpiresAtUtc <= clock.GetUtcNow())
            throw new InvalidDataException("The Controller returned an empty or expired source challenge.");

        var sourceRequestId = StableRequestId("source-binding", binding.OperationId, attemptNumber);
        var bootId = bootIdentity.ReadBootId();
        if (string.IsNullOrWhiteSpace(bootId) || bootId.Length > 256 || bootId.Any(char.IsControl))
            throw new InvalidDataException("The native boot identity is missing or malformed.");
        var evidence = new AgentUpdateSourceEvidence(identity.MachineId, identity.KeyFingerprint,
            binding.OperationId, binding.ChainId, binding.HeadOperationId, binding.Generation,
            binding.TargetVersion, binding.TargetSha256, challenge.ChallengeId, challengeRequestId,
            sourceRequestId, parameters.HelperBuildSha256, parameters.HelperRuntime, parameters.HelperProtocol,
            challenge.RuntimeEpoch, source.Version, backup.BackupOwnerOperationId, backup.BackupDigest,
            backup.InventoryDigest, backup.InventoryDigest, bootId, "backupPublished");
        var evidenceSignature = identity.SignSourceEvidence(evidence);
        var bindingRequest = BuildBindingRequest(binding, parameters, evidence, challenge, evidenceSignature);
        var intent = new MaintenanceSourceBindingIntent(evidence, attemptNumber, challenge.ChallengeId, challenge.Challenge,
            challenge.RuntimeEpoch, challenge.ExpiresAtUtc, challengeRequestBodyDigest, evidenceSignature, sourceRequestId,
            ComputeBodyDigest(bindingRequest));
        return journal.RecordSourceBindingIntent(binding.OperationId, intent).Operations.Single().SourceBindingIntent
            ?? throw new IOException("The signed source-binding intent was not durably recorded.");
    }

    private static AgentUpdateSourceBindingRequest BuildBindingRequest(
        MaintenanceOperationBinding binding,
        MaintenanceSourceBindingParameters parameters,
        MaintenanceSourceBindingIntent intent) =>
        BuildBindingRequest(binding, parameters, intent.Evidence, new(
            intent.ChallengeId, intent.Challenge, intent.RuntimeEpoch, intent.ChallengeExpiresAtUtc),
            intent.EvidenceSignature);

    private static AgentUpdateSourceBindingRequest BuildBindingRequest(
        MaintenanceOperationBinding binding,
        MaintenanceSourceBindingParameters parameters,
        AgentUpdateSourceEvidence evidence,
        AgentUpdateSourceChallengeResponse challenge,
        string evidenceSignature) =>
        new(parameters.Ticket, parameters.ReleaseVersion, binding.TargetVersion, binding.TargetSha256,
            challenge.ChallengeId, challenge.Challenge, evidence.ChallengeRequestId, evidence.SourceRequestId,
            evidence.HelperBuildSha256, evidence.HelperRuntime, evidence.HelperProtocol, challenge.RuntimeEpoch,
            evidence.SourceVersion, evidence.BackupOwnerOperationId, evidence.BackupDigest,
            evidence.InventoryDigest, evidence.InstalledTreeDigest, evidence.NativeBootId,
            evidence.JournalPhase, binding.ChainId, binding.HeadOperationId, binding.Generation,
            EvidenceSignature: evidenceSignature);

    private static void ValidateAcknowledgement(
        MaintenanceOperationBinding binding,
        string machineId,
        AgentUpdateSourceBindingRequest request,
        string bodyDigest,
        AgentUpdateOperationDto response)
    {
        if (response.OperationId != binding.OperationId || response.MachineId != machineId ||
            response.ChainId != binding.ChainId || response.CurrentHeadOperationId != binding.HeadOperationId ||
            response.Generation != binding.Generation || !response.Held || response.Status != "installing" ||
            response.ReleaseVersion != request.ReleaseVersion ||
            response.TargetVersion != binding.TargetVersion || !FixedHexEquals(response.TargetPackageSha256, binding.TargetSha256) ||
            response.SourceVersion != request.SourceVersion ||
            response.SourceEvidenceDigest is null || !FixedHexEquals(response.SourceEvidenceDigest, bodyDigest) ||
            response.BackupOwnerOperationId != request.BackupOwnerOperationId ||
            response.BackupDigest is null || !FixedHexEquals(response.BackupDigest, request.BackupDigest))
            throw new InvalidOperationException("The Controller source-binding acknowledgement does not match the signed request and current journal head.");
    }

    private static void ValidateParameters(MaintenanceSourceBindingParameters parameters)
    {
        if (parameters.Ticket.Length != 43 || parameters.Ticket.Any(character =>
                !char.IsAsciiLetterOrDigit(character) && character is not ('_' or '-')) ||
            string.IsNullOrWhiteSpace(parameters.ReleaseVersion) || parameters.ReleaseVersion.Length > 128 ||
            parameters.ReleaseVersion.Any(character => !char.IsAsciiLetterOrDigit(character) && character is not ('.' or '+' or '-' or '_')) ||
            !IsDigest(parameters.HelperBuildSha256) || parameters.HelperRuntime is not ("win-x64" or "win-arm64") ||
            parameters.HelperProtocol != 1)
            throw new InvalidDataException("The fixed source-binding request parameters are malformed.");
    }

    private static int NextAttemptNumber(MaintenanceJournalOperation operation) =>
        (operation.RejectedSourceChallengeAttempts?.Count ?? 0) +
        (operation.RejectedSourceBindingIntents?.Count ?? 0);

    private static string StableRequestId(string purpose, string operationId, int attemptNumber)
    {
        var digest = SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(
            "KamenRaider.AgentMaintenance." + purpose + ".v1\n" + operationId + "\n" +
            attemptNumber.ToString(System.Globalization.CultureInfo.InvariantCulture)));
        return purpose + "-" + Convert.ToHexStringLower(digest);
    }

    private static string ComputeBodyDigest<T>(T request) =>
        Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(request, JsonOptions)));

    private static string ComputeBodyDigest(AgentUpdateSourceBindingRequest request) =>
        Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(request, JsonOptions)));

    private static bool IsDigest(string? value) =>
        value is { Length: 64 } && value.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f' or >= 'A' and <= 'F');

    private static bool FixedHexEquals(string? first, string? second) =>
        first is not null && second is not null && IsDigest(first) && IsDigest(second) &&
        CryptographicOperations.FixedTimeEquals(System.Text.Encoding.ASCII.GetBytes(first.ToUpperInvariant()),
            System.Text.Encoding.ASCII.GetBytes(second.ToUpperInvariant()));
}

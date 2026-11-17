using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using KamenRaider.Contracts;

namespace KamenRaider.AgentMaintenance;

public sealed record MaintenanceLifecycleRequest(
    int SchemaVersion,
    string Command,
    string OperationId,
    string ChainId,
    string HeadOperationId,
    long Generation,
    string ControllerOrigin,
    string EnrollmentKeyFingerprint,
    int HelperProtocol,
    string TargetVersion,
    string TargetRuntime,
    string TargetPackageSha256,
    string ReleaseVersion,
    string Ticket,
    string? HelperBuildSha256 = null);

public interface IMaintenanceLifecycleWorkStore
{
    MaintenanceLifecycleRequest ReadLifecycleRequest(string expectedCommand);
}

public static class MaintenanceLifecycleRequestParser
{
    private const int MaximumRequestBytes = 16 * 1024;
    private static readonly Regex VersionPattern = new(
        "\\A(?:[0-9]+\\.[0-9]+\\.[0-9]+|1\\.0\\.0\\+g[0-9a-f]{12,40})\\z",
        RegexOptions.CultureInvariant);
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = false,
        MaxDepth = 4
    };
    private static readonly HashSet<string> CommonFields = new(StringComparer.Ordinal)
    {
        "schemaVersion", "command", "operationId", "chainId", "headOperationId", "generation",
        "controllerOrigin", "enrollmentKeyFingerprint", "helperProtocol", "targetVersion",
        "targetRuntime", "targetPackageSha256", "releaseVersion", "ticket"
    };

    public static MaintenanceLifecycleRequest Parse(byte[] bytes, string expectedCommand)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        if (expectedCommand is not ("start" or "bind-source" or "report") ||
            bytes.Length is <= 0 or > MaximumRequestBytes)
            throw new InvalidDataException("The typed maintenance lifecycle request exceeds its limit or names an unsupported operation.");

        try
        {
            using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions
            {
                MaxDepth = 4,
                CommentHandling = JsonCommentHandling.Disallow,
                AllowTrailingCommas = false
            });
            if (document.RootElement.ValueKind != JsonValueKind.Object)
                throw new InvalidDataException("The typed maintenance lifecycle request must be a JSON object.");

            var required = new HashSet<string>(CommonFields, StringComparer.Ordinal);
            if (expectedCommand == "bind-source") required.Add("helperBuildSha256");
            var found = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var property in document.RootElement.EnumerateObject())
                if (!required.Contains(property.Name) || !found.Add(property.Name))
                    throw new InvalidDataException("The typed maintenance lifecycle request contains an unknown or duplicate field.");
            if (!found.SetEquals(required))
                throw new InvalidDataException("The typed maintenance lifecycle request is incomplete.");

            var request = JsonSerializer.Deserialize<MaintenanceLifecycleRequest>(bytes, JsonOptions)
                ?? throw new InvalidDataException("The typed maintenance lifecycle request is empty.");
            return Validate(request, expectedCommand);
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("The typed maintenance lifecycle request is malformed.", exception);
        }
    }

    private static MaintenanceLifecycleRequest Validate(MaintenanceLifecycleRequest request, string expectedCommand)
    {
        if (request.SchemaVersion != 1 || request.Command != expectedCommand ||
            !IsIdentifier(request.OperationId) || request.HeadOperationId != request.OperationId ||
            !IsIdentifier(request.ChainId) || request.Generation <= 0 ||
            !VersionPattern.IsMatch(request.TargetVersion) || request.TargetRuntime is not ("win-x64" or "win-arm64") ||
            !IsDigest(request.TargetPackageSha256) || !IsDigest(request.EnrollmentKeyFingerprint) ||
            request.HelperProtocol != 1 || !IsIdentifier(request.ReleaseVersion, allowPlus: true) ||
            request.Ticket is not { Length: 43 } || request.Ticket.Any(character =>
                !char.IsAsciiLetterOrDigit(character) && character is not ('_' or '-')) ||
            !ParseOrigin(request.ControllerOrigin, out _))
            throw new InvalidDataException("The typed maintenance lifecycle request is malformed or does not match the requested operation.");

        if (expectedCommand == "bind-source" && !IsDigest(request.HelperBuildSha256))
            throw new InvalidDataException("The source-binding request does not identify a pinned helper build.");
        if (expectedCommand != "bind-source" && request.HelperBuildSha256 is not null)
            throw new InvalidDataException("The typed maintenance lifecycle request contains an unexpected helper build hash.");
        return request;
    }

    private static bool IsIdentifier(string? value, bool allowPlus = false) =>
        !string.IsNullOrWhiteSpace(value) && value.Length <= 128 &&
        value.All(character => char.IsAsciiLetterOrDigit(character) || character is '.' or '_' or '-' || allowPlus && character == '+');

    private static bool IsDigest(string? value) =>
        value is { Length: 64 } && value.All(Uri.IsHexDigit);

    internal static bool ParseOrigin(string? value, out Uri? origin)
    {
        origin = null;
        if (!Uri.TryCreate(value, UriKind.Absolute, out var parsed) || parsed.Scheme != Uri.UriSchemeHttps ||
            !string.IsNullOrEmpty(parsed.UserInfo) || !string.IsNullOrEmpty(parsed.Query) ||
            !string.IsNullOrEmpty(parsed.Fragment) || parsed.AbsolutePath != "/" ||
            !string.Equals(parsed.GetLeftPart(UriPartial.Authority) + "/", value, StringComparison.Ordinal))
            return false;
        origin = parsed;
        return true;
    }
}

public interface IMaintenanceStartCommand
{
    AgentMaintenanceCommandResult Execute();
}

public interface IMaintenanceReportCommand
{
    AgentMaintenanceCommandResult Execute();
}

public interface IMaintenanceStartClient
{
    Task<AgentUpdateOperationDto> StartAsync(
        string controllerOrigin,
        string machineId,
        string operationId,
        AgentUpdateTicketRequest request,
        CancellationToken cancellationToken);
}

public interface IMaintenanceReportClient
{
    Task<AgentUpdateOperationDto> ReportAsync(
        string controllerOrigin,
        string machineId,
        string operationId,
        AgentUpdateStatusReportRequest request,
        CancellationToken cancellationToken);
}

public sealed class MaintenanceStartCommand(
    IMaintenanceLifecycleWorkStore workStore,
    ITrustedIdentitySource identitySource,
    IMaintenanceJournalStore journalStore,
    IMaintenanceStartClient client,
    IMaintenanceApplyProcessExclusionProvider exclusionProvider) : IMaintenanceStartCommand
{
    public AgentMaintenanceCommandResult Execute()
    {
        var exclusion = exclusionProvider.Acquire();
        if (exclusion is null)
            return new("unavailable", "legacyProcessExclusionUnqualified", null, null, null, null, null, null);

        using (exclusion)
        using (var identity = TrustedIdentity.ReadExisting(identitySource))
        {
            var request = workStore.ReadLifecycleRequest("start");
            MaintenanceLifecycleValidation.VerifyIdentity(request, identity);
            MaintenanceLifecycleValidation.VerifyNativeTargetRuntime(request.TargetRuntime);

            var binding = MaintenanceLifecycleValidation.CreateBinding(request);
            exclusion.VerifyContinuouslyHeld(request.OperationId);
            var journal = new MaintenanceJournal(journalStore, request.ChainId);
            var snapshot = journal.StartOrSuccessor(binding);
            var operation = MaintenanceJournal.RequireHead(snapshot, request.OperationId);
            if (operation.Phase == "inspected")
            {
                var startRequest = new AgentUpdateTicketRequest(request.Ticket, request.ReleaseVersion,
                    request.TargetVersion, request.TargetPackageSha256,
                    MaintenanceLifecycleValidation.StartRequestId(binding));
                var response = client.StartAsync(request.ControllerOrigin, identity.MachineId,
                        request.OperationId, startRequest, CancellationToken.None)
                    .GetAwaiter().GetResult();
                MaintenanceLifecycleValidation.VerifyAcknowledgement(response, binding, identity.MachineId,
                    request.ReleaseVersion, "installing");
                exclusion.VerifyContinuouslyHeld(request.OperationId);
                snapshot = journal.Advance(request.OperationId, "controllerStarted");
                operation = MaintenanceJournal.RequireHead(snapshot, request.OperationId);
            }

            if (operation.Phase == "inspected")
                return new("unavailable", "controllerStartUnconfirmed", null, null, null, null, null, null);

            return new("ok", null, null, request.TargetVersion, request.TargetRuntime, null, null, null,
                ApplyOutcome: "started");
        }
    }
}

public sealed class MaintenanceReportCommand(
    IMaintenanceLifecycleWorkStore workStore,
    ITrustedIdentitySource identitySource,
    IMaintenanceJournalStore journalStore,
    IMaintenanceReportClient client,
    IMaintenanceApplyProcessExclusionProvider exclusionProvider,
    IMaintenanceTerminalStateVerifier? terminalStateVerifier = null,
    IMaintenanceOperationFenceVerifier? fenceVerifier = null,
    Func<string, Action, IMaintenanceTerminalStateVerifier>? terminalStateVerifierFactory = null) : IMaintenanceReportCommand
{
    public AgentMaintenanceCommandResult Execute()
    {
        var exclusion = exclusionProvider.Acquire();
        if (exclusion is null)
            return new("unavailable", "legacyProcessExclusionUnqualified", null, null, null, null, null, null);

        using (exclusion)
        using (var identity = TrustedIdentity.ReadExisting(identitySource))
        {
            var request = workStore.ReadLifecycleRequest("report");
            MaintenanceLifecycleValidation.VerifyIdentity(request, identity);
            var binding = MaintenanceLifecycleValidation.CreateBinding(request);
            var journal = new MaintenanceJournal(journalStore, request.ChainId);
            var snapshot = journal.Read()
                ?? throw new InvalidOperationException("The maintenance journal is missing; the outcome remains held.");
            if (snapshot.HeadOperationId != request.OperationId || snapshot.Generation != request.Generation)
                return new("unavailable", "reportBindingUnverifiable", null, null, null, null, null, null);
            MaintenanceJournalOperation operation;
            try
            {
                operation = MaintenanceJournal.RequireHead(snapshot, request.OperationId);
            }
            catch (InvalidOperationException)
            {
                return new("unavailable", "reportBindingUnverifiable", null, null, null, null, null, null);
            }
            if (operation.Binding != binding)
                return new("unavailable", "reportBindingUnverifiable", null, null, null, null, null, null);
            var status = operation.Phase switch
            {
                "servicesStarted" => "awaitingHeartbeat",
                "rollbackServicesStarted" => "failed",
                "outcomeReported" when operation.PhaseHistory.Contains("rollbackServicesStarted", StringComparer.Ordinal) => "failed",
                "outcomeReported" => "awaitingHeartbeat",
                _ => null
            };
            if (status is null)
                return new("unavailable", "outcomeNotReady", null, null, null, null, null, null);

            var terminalOutcome = status == "failed"
                ? MaintenanceTerminalOutcome.RolledBack
                : MaintenanceTerminalOutcome.Applied;
            Action verifyHeld = () =>
            {
                exclusion.VerifyContinuouslyHeld(request.OperationId);
                fenceVerifier?.VerifyHeld(binding);
            };
            try
            {
                var verifier = terminalStateVerifier
                    ?? terminalStateVerifierFactory?.Invoke(request.ChainId, verifyHeld)
                    ?? throw new InvalidOperationException("The terminal state verifier is unavailable.");
                verifyHeld();
                verifier.VerifyAsync(binding, operation, terminalOutcome, expectedTarget: null,
                    verifyHeld, CancellationToken.None)
                    .GetAwaiter().GetResult();
            }
            catch (Exception exception) when (exception is not OutOfMemoryException and not StackOverflowException and not AccessViolationException)
            {
                return new("unavailable", "terminalStateUnverifiable", null, null, null, null, null, null);
            }

            var report = new AgentUpdateStatusReportRequest(request.Ticket, request.ReleaseVersion,
                request.TargetVersion, request.TargetPackageSha256, status);
            exclusion.VerifyContinuouslyHeld(request.OperationId);
            fenceVerifier?.VerifyHeld(binding);
            var response = client.ReportAsync(request.ControllerOrigin, identity.MachineId,
                    request.OperationId, report, CancellationToken.None)
                .GetAwaiter().GetResult();
            MaintenanceLifecycleValidation.VerifyAcknowledgement(response, binding, identity.MachineId,
                request.ReleaseVersion, status);
            if (operation.Phase != "outcomeReported")
            {
                exclusion.VerifyContinuouslyHeld(request.OperationId);
                fenceVerifier?.VerifyHeld(binding);
                _ = journal.Advance(request.OperationId, "outcomeReported");
            }

            return new("ok", null, null, request.TargetVersion, request.TargetRuntime, null, null, null,
                ApplyOutcome: status);
        }
    }
}

public interface IMaintenanceSourceBindingAuthorization
{
    void VerifyRequestBinding(MaintenanceLifecycleRequest request);
    void VerifyHeldAndQuiesced(string operationId);
    void VerifyHeld(string operationId);
}

public interface IMaintenanceSourceBindingOperationFactory
{
    MaintenanceSourceBindingOperation Create(
        MaintenanceLifecycleRequest request,
        MaintenanceJournal journal,
        TrustedIdentity identity,
        IMaintenanceApplyProcessExclusionLease exclusion,
        IMaintenanceSourceBindingAuthorization authorization);
}

public interface IMaintenanceHelperBuildIdentityProvider
{
    string ReadSha256();
}

public sealed class WindowsMaintenanceHelperBuildIdentityProvider : IMaintenanceHelperBuildIdentityProvider
{
    public string ReadSha256()
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("The maintenance helper identity check requires Windows.");
        var path = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path))
            throw new InvalidDataException("The running maintenance helper path is unavailable.");
        using var image = WindowsMaintenanceWorkStore.OpenArchiveStream(path);
        return Convert.ToHexStringLower(SHA256.HashData(image));
    }
}

public sealed class MaintenanceSourceBindingOperationFactory(
    IMaintenanceBackupSecurity security,
    string sourceRoot,
    string backupRoot,
    IMaintenanceSourceBindingClient client,
    IMaintenanceBootIdentityProvider bootIdentity) : IMaintenanceSourceBindingOperationFactory
{
    public MaintenanceSourceBindingOperation Create(
        MaintenanceLifecycleRequest request,
        MaintenanceJournal journal,
        TrustedIdentity identity,
        IMaintenanceApplyProcessExclusionLease exclusion,
        IMaintenanceSourceBindingAuthorization authorization)
    {
        var inspector = new InstalledAgentSourceInspector(sourceRoot, security);
        var backupAuthorization = new BoundBackupAuthorization(request, exclusion, authorization);
        var publisher = new MaintenanceBackupPublisher(sourceRoot, backupRoot, security, backupAuthorization);
        var backupOperation = new MaintenanceBackupOperation(sourceRoot, inspector, publisher, journal);
        return new(backupOperation, journal, client, identity, bootIdentity);
    }

    private sealed class BoundBackupAuthorization(
        MaintenanceLifecycleRequest request,
        IMaintenanceApplyProcessExclusionLease exclusion,
        IMaintenanceSourceBindingAuthorization authorization) : IMaintenanceBackupAuthorization
    {
        public void VerifyHeldAndQuiesced(string operationId)
        {
            if (!string.Equals(operationId, request.OperationId, StringComparison.Ordinal))
                throw new InvalidOperationException("The source backup authorization belongs to another operation.");
            exclusion.VerifyContinuouslyHeld(operationId);
            authorization.VerifyRequestBinding(request);
            authorization.VerifyHeldAndQuiesced(operationId);
        }

        public void VerifyHeld(string operationId)
        {
            if (!string.Equals(operationId, request.OperationId, StringComparison.Ordinal))
                throw new InvalidOperationException("The source backup authorization belongs to another operation.");
            exclusion.VerifyContinuouslyHeld(operationId);
            authorization.VerifyRequestBinding(request);
            authorization.VerifyHeld(operationId);
        }
    }
}

public sealed class MaintenanceSourceBindingCommand(
    IMaintenanceLifecycleWorkStore workStore,
    ITrustedIdentitySource identitySource,
    IMaintenanceJournalStore journalStore,
    IMaintenanceApplyProcessExclusionProvider exclusionProvider,
    IMaintenanceSourceBindingAuthorization authorization,
    IMaintenanceHelperBuildIdentityProvider helperIdentity,
    IMaintenanceSourceBindingOperationFactory operationFactory,
    IMaintenanceSourceBindingPreparation? preparation = null,
    IMaintenanceOperationFenceVerifier? fenceVerifier = null) : IMaintenanceSourceBindingCommand
{
    public AgentMaintenanceCommandResult Execute()
    {
        var exclusion = exclusionProvider.Acquire();
        if (exclusion is null)
            return new("unavailable", "legacyProcessExclusionUnqualified", null, null, null, null, null, null);

        using (exclusion)
        using (var identity = TrustedIdentity.ReadExisting(identitySource))
        {
            var request = workStore.ReadLifecycleRequest("bind-source");
            MaintenanceLifecycleValidation.VerifyIdentity(request, identity);
            if (!string.Equals(helperIdentity.ReadSha256(), request.HelperBuildSha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("The running helper bytes do not match the pinned source-binding request.");

            var binding = MaintenanceLifecycleValidation.CreateBinding(request);
            var journal = new MaintenanceJournal(journalStore, request.ChainId);
            var snapshot = journal.Read()
                ?? throw new InvalidOperationException("The maintenance journal is missing; source binding remains held.");
            if (snapshot.HeadOperationId != request.OperationId || snapshot.Generation != request.Generation)
                return new("unavailable", "sourceBindingHeadUnverifiable", null, null, null, null, null, null);
            MaintenanceJournalOperation headOperation;
            try
            {
                headOperation = MaintenanceJournal.RequireHead(snapshot, request.OperationId);
            }
            catch (InvalidOperationException)
            {
                return new("unavailable", "sourceBindingHeadUnverifiable", null, null, null, null, null, null);
            }
            if (headOperation.Binding != binding)
                return new("unavailable", "sourceBindingHeadUnverifiable", null, null, null, null, null, null);

            exclusion.VerifyContinuouslyHeld(request.OperationId);
            authorization.VerifyRequestBinding(request);

            var phase = headOperation.Phase;
            if (phase is "controllerStarted" or "targetValidated")
            {
                if (preparation is null)
                    return new("unavailable", "targetOrQuiescenceUnverified", null, null, null, null, null, null);
                try
                {
                    preparation.Prepare(request, journal, exclusion, authorization);
                }
                catch (Exception exception) when (exception is not OutOfMemoryException and not StackOverflowException and not AccessViolationException)
                {
                    return new("unavailable", "targetOrQuiescenceUnverified", null, null, null, null, null, null);
                }
                snapshot = journal.Read()
                    ?? throw new InvalidOperationException("The maintenance journal disappeared during target preparation.");
                phase = MaintenanceJournal.Head(snapshot).Phase;
            }
            else if (preparation is not null && phase == "servicesQuiesced")
            {
                try
                {
                    preparation.Prepare(request, journal, exclusion, authorization);
                }
                catch (Exception exception) when (exception is not OutOfMemoryException and not StackOverflowException and not AccessViolationException)
                {
                    return new("unavailable", "targetOrQuiescenceUnverified", null, null, null, null, null, null);
                }
                snapshot = journal.Read()
                    ?? throw new InvalidOperationException("The maintenance journal disappeared during target preparation.");
                phase = MaintenanceJournal.Head(snapshot).Phase;
            }

            if (phase is not ("servicesQuiesced" or "backupPublished" or "sourceBound"))
                return new("unavailable", "targetOrQuiescenceUnverified", null, null, null, null, null, null);
            var boundAuthorization = new FenceBoundSourceBindingAuthorization(request, authorization, fenceVerifier);
            boundAuthorization.VerifyHeldAndQuiesced(request.OperationId);
            var operation = operationFactory.Create(request, journal, identity, exclusion, boundAuthorization);
            var result = operation.BindAsync(new(request.Ticket, request.ReleaseVersion,
                    request.HelperBuildSha256!, request.TargetRuntime, request.HelperProtocol), CancellationToken.None)
                .GetAwaiter().GetResult();
            exclusion.VerifyContinuouslyHeld(request.OperationId);
            boundAuthorization.VerifyHeld(request.OperationId);
            return new("ok", null, null, result.SourceVersion, request.TargetRuntime,
                result.InventoryDigest, null, null, ApplyOutcome: "sourceBound");
        }
    }

    private sealed class FenceBoundSourceBindingAuthorization(
        MaintenanceLifecycleRequest request,
        IMaintenanceSourceBindingAuthorization authorization,
        IMaintenanceOperationFenceVerifier? fenceVerifier) : IMaintenanceSourceBindingAuthorization
    {
        public void VerifyRequestBinding(MaintenanceLifecycleRequest actual)
        {
            if (actual != request)
                throw new InvalidOperationException("The source-binding authorization request changed.");
            authorization.VerifyRequestBinding(actual);
        }

        public void VerifyHeldAndQuiesced(string operationId)
        {
            VerifyOperation(operationId);
            authorization.VerifyHeldAndQuiesced(operationId);
            fenceVerifier?.VerifyHeld(MaintenanceLifecycleValidation.CreateBinding(request));
        }

        public void VerifyHeld(string operationId)
        {
            VerifyOperation(operationId);
            authorization.VerifyHeld(operationId);
            fenceVerifier?.VerifyHeld(MaintenanceLifecycleValidation.CreateBinding(request));
        }

        private void VerifyOperation(string operationId)
        {
            if (operationId != request.OperationId)
                throw new InvalidOperationException("The source-binding authorization belongs to another operation.");
        }
    }
}

public sealed class UnavailableMaintenanceSourceBindingAuthorization : IMaintenanceSourceBindingAuthorization
{
    public void VerifyRequestBinding(MaintenanceLifecycleRequest request) =>
        throw new InvalidOperationException("The Controller maintenance lease authorization is unavailable.");

    public void VerifyHeldAndQuiesced(string operationId) =>
        throw new InvalidOperationException("The Controller maintenance lease authorization is unavailable.");

    public void VerifyHeld(string operationId) =>
        throw new InvalidOperationException("The Controller maintenance lease authorization is unavailable.");
}

internal static class MaintenanceLifecycleValidation
{
    public const int SupportedTargetProtocol = 2;

    public static MaintenanceOperationBinding CreateBinding(MaintenanceLifecycleRequest request) => new(
        request.OperationId, request.ChainId, request.HeadOperationId, request.Generation,
        request.ControllerOrigin, request.EnrollmentKeyFingerprint.ToLowerInvariant(), request.HelperProtocol,
        request.TargetRuntime, request.TargetVersion, request.TargetPackageSha256.ToLowerInvariant());

    public static void VerifyIdentity(MaintenanceLifecycleRequest request, TrustedIdentity identity)
    {
        if (!string.Equals(request.EnrollmentKeyFingerprint, identity.KeyFingerprint, StringComparison.OrdinalIgnoreCase))
            throw new UnauthorizedAccessException("The maintenance request does not match the existing enrolled identity.");
    }

    public static void VerifyNativeTargetRuntime(string targetRuntime)
    {
        var nativeRuntime = System.Runtime.InteropServices.RuntimeInformation.OSArchitecture switch
        {
            System.Runtime.InteropServices.Architecture.X64 => "win-x64",
            System.Runtime.InteropServices.Architecture.Arm64 => "win-arm64",
            _ => throw new PlatformNotSupportedException("The maintenance helper requires a supported native Windows architecture.")
        };
        if (!string.Equals(targetRuntime, nativeRuntime, StringComparison.Ordinal))
            throw new InvalidOperationException("The target package runtime does not match the native host architecture.");
    }

    public static string StartRequestId(MaintenanceOperationBinding binding)
    {
        var canonical = "KamenRaider.AgentMaintenance.start.v1\n" + binding.OperationId + "\n" +
            binding.ChainId + "\n" + binding.Generation.ToString(System.Globalization.CultureInfo.InvariantCulture);
        return "start-" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
    }

    public static void VerifyAcknowledgement(
        AgentUpdateOperationDto response,
        MaintenanceOperationBinding binding,
        string machineId,
        string releaseVersion,
        string expectedStatus)
    {
        if (response.OperationId != binding.OperationId || response.MachineId != machineId ||
            response.ChainId != binding.ChainId || response.CurrentHeadOperationId != binding.HeadOperationId ||
            response.Generation != binding.Generation || !response.Held || response.Status != expectedStatus ||
            response.ReleaseVersion != releaseVersion || response.TargetVersion != binding.TargetVersion ||
            response.TargetRuntime != binding.TargetRuntime ||
            response.TargetProtocol != SupportedTargetProtocol ||
            !FixedHexEquals(response.TargetPackageSha256, binding.TargetSha256))
            throw new InvalidOperationException("The Controller maintenance acknowledgement does not match the scoped request and current journal head.");
    }

    private static bool FixedHexEquals(string? left, string? right) =>
        left is { Length: 64 } && right is { Length: 64 } &&
        left.All(Uri.IsHexDigit) && right.All(Uri.IsHexDigit) &&
        CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(left.ToUpperInvariant()),
            Encoding.ASCII.GetBytes(right.ToUpperInvariant()));
}

public sealed class HttpMaintenanceLifecycleClient : IMaintenanceStartClient, IMaintenanceReportClient
{
    private const int MaximumResponseBytes = 64 * 1024;
    private static readonly TimeSpan DefaultRequestTimeout = TimeSpan.FromSeconds(30);
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { MaxDepth = 8 };
    private readonly HttpClient httpClient;
    private readonly TimeSpan requestTimeout;

    public HttpMaintenanceLifecycleClient(HttpClient httpClient) : this(httpClient, DefaultRequestTimeout) { }

    internal HttpMaintenanceLifecycleClient(HttpClient httpClient, TimeSpan requestTimeout)
    {
        this.httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        this.requestTimeout = ValidateRequestTimeout(requestTimeout);
    }

    public static HttpMaintenanceLifecycleClient CreateDefault()
    {
        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            ConnectTimeout = TimeSpan.FromSeconds(15),
            PooledConnectionLifetime = TimeSpan.FromMinutes(5)
        };
        return new(new HttpClient(handler, disposeHandler: true) { Timeout = TimeSpan.FromSeconds(30) });
    }

    public Task<AgentUpdateOperationDto> StartAsync(
        string controllerOrigin,
        string machineId,
        string operationId,
        AgentUpdateTicketRequest request,
        CancellationToken cancellationToken) =>
        PostAsync(controllerOrigin, machineId, operationId, "start", request, cancellationToken);

    public Task<AgentUpdateOperationDto> ReportAsync(
        string controllerOrigin,
        string machineId,
        string operationId,
        AgentUpdateStatusReportRequest request,
        CancellationToken cancellationToken) =>
        PostAsync(controllerOrigin, machineId, operationId, "status", request, cancellationToken);

    private async Task<AgentUpdateOperationDto> PostAsync<TRequest>(
        string controllerOrigin,
        string machineId,
        string operationId,
        string action,
        TRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var origin = ValidateOrigin(controllerOrigin);
        var escapedMachineId = EscapeIdentifier(machineId);
        var escapedOperationId = EscapeIdentifier(operationId);
        var path = $"/agent/machines/{escapedMachineId}/agent-updates/{escapedOperationId}/{action}";
        var uri = new Uri(origin, path);
        using var message = new HttpRequestMessage(HttpMethod.Post, uri)
        {
            Content = new ByteArrayContent(JsonSerializer.SerializeToUtf8Bytes(request, JsonOptions))
        };
        message.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        message.Headers.CacheControl = new CacheControlHeaderValue { NoStore = true };

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(requestTimeout);
        try
        {
            using var response = await httpClient.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, deadline.Token)
                .ConfigureAwait(false);
            if (response.RequestMessage?.RequestUri is { } finalUri && finalUri != uri)
                throw new InvalidOperationException("The Controller redirected a scoped maintenance request.");
            if (response.StatusCode != HttpStatusCode.OK)
            {
                if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                    throw new UnauthorizedAccessException("The Controller rejected the scoped maintenance credential.");
                if (response.StatusCode == HttpStatusCode.Conflict)
                    throw new InvalidOperationException("The Controller rejected the maintenance state transition.");
                if (response.StatusCode == HttpStatusCode.RequestEntityTooLarge)
                    throw new InvalidDataException("The Controller returned an oversized maintenance response.");
                throw new IOException($"The Controller maintenance endpoint returned HTTP {(int)response.StatusCode}.");
            }

            if (response.Content.Headers.ContentLength is > MaximumResponseBytes)
                throw new InvalidDataException("The Controller maintenance response exceeds its size limit.");
            await using var input = await response.Content.ReadAsStreamAsync(deadline.Token).ConfigureAwait(false);
            using var output = new MemoryStream();
            var buffer = new byte[4096];
            while (true)
            {
                var read = await input.ReadAsync(buffer.AsMemory(), deadline.Token).ConfigureAwait(false);
                if (read == 0) break;
                if (output.Length + read > MaximumResponseBytes)
                    throw new InvalidDataException("The Controller maintenance response exceeds its size limit.");
                output.Write(buffer, 0, read);
            }

            deadline.Token.ThrowIfCancellationRequested();
            try
            {
                var result = JsonSerializer.Deserialize<AgentUpdateOperationDto>(output.ToArray(), JsonOptions)
                    ?? throw new InvalidDataException("The Controller maintenance response was empty.");
                deadline.Token.ThrowIfCancellationRequested();
                return result;
            }
            catch (JsonException exception)
            {
                throw new InvalidDataException("The Controller maintenance response was malformed.", exception);
            }
        }
        catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException("The Controller maintenance request exceeded its bounded deadline.", exception);
        }
    }

    private static Uri ValidateOrigin(string value)
    {
        if (!MaintenanceLifecycleRequestParser.ParseOrigin(value, out var origin))
            throw new InvalidDataException("The maintenance Controller origin is not a canonical HTTPS origin.");
        return origin!;
    }

    private static string EscapeIdentifier(string value)
    {
        if (!IsSafeIdentifier(value))
            throw new InvalidDataException("The scoped maintenance route contains an invalid identifier.");
        return Uri.EscapeDataString(value);
    }

    private static bool IsSafeIdentifier(string? value) =>
        !string.IsNullOrWhiteSpace(value) && value.Length <= 128 &&
        value.All(character => char.IsAsciiLetterOrDigit(character) || character is '.' or '_' or '-');

    private static TimeSpan ValidateRequestTimeout(TimeSpan value)
    {
        if (value <= TimeSpan.Zero || value > TimeSpan.FromDays(1))
            throw new ArgumentOutOfRangeException(nameof(value), "The maintenance HTTP deadline must be positive and bounded.");
        return value;
    }
}

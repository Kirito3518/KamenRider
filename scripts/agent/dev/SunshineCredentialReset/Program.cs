using System.ComponentModel;
using System.Formats.Asn1;
using System.Diagnostics;
using System.Net;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Security.Principal;
using System.ServiceProcess;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using KamenRaider.Agent.Sunshine;

namespace SunshineCredentialReset;

/// <summary>
/// One-shot local operator tool that prepares the installed Foundation Sunshine management
/// endpoint for the KamenRaider Agent: it provisions a TLS certificate that Windows can
/// validate for https://localhost, trusts it for the machine service and the interactive
/// user, resets the Sunshine Web manager credentials, and stores the same credentials in the
/// Agent DPAPI credential store. Every mutation is backed up and rolled back on failure.
///
/// The plaintext password is never written to standard output, a result file or a log.
/// </summary>
[SupportedOSPlatform("windows")]
internal static class Program
{
    private const string SunshineServiceName = "SunshineService";
    private const int PasswordLength = 24;
    private const int CertificateLifetimeYears = 10;
    private const int MaximumCredentialNoteBytes = 16 * 1024;

    private static readonly TimeSpan SunshineCommandTimeout = TimeSpan.FromSeconds(90);
    private static readonly TimeSpan CertificateClockSkew = TimeSpan.FromDays(1);
    private static readonly TimeSpan MinimumCertificateRemaining = TimeSpan.FromDays(30);
    private static readonly string[] LocalHostNames = ["localhost"];
    private static readonly char[] PasswordAlphabet =
        "ABCDEFGHJKLMNPQRSTUVWXYZabcdefghijkmnopqrstuvwxyz23456789".ToCharArray();
    private static readonly JsonSerializerOptions ResultJsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    };

    private sealed record SetupResult(
        string Operation,
        string Status,
        string? FailureCategory = null,
        string? FailureDetail = null,
        string? SunshineRoot = null,
        string? SunshineServiceState = null,
        string? BackupDirectory = null,
        string? TlsAction = null,
        string? TlsThumbprint = null,
        bool? TrustedInLocalMachineRoot = null,
        bool? TrustedInCurrentUserRoot = null,
        string? CredentialStateVerification = null,
        string? Username = null,
        bool? PairingStatePreserved = null,
        bool? AgentStoreWritten = null,
        bool? AgentStoreVerified = null,
        string? CredentialNotePath = null,
        int? SunshineCommandExitCode = null,
        string? RollbackStatus = null);

    private sealed record Options(
        bool Apply,
        bool ProvisionTls,
        string SunshineRoot,
        string ResultFile,
        string? AgentDataDirectory,
        string? UserName,
        string? CredentialNotePath,
        string? BackupRoot);

    public static int Main(string[] args)
    {
        Options options;
        try
        {
            options = ParseOptions(args);
        }
        catch (ArgumentException exception)
        {
            Console.Error.WriteLine(exception.Message);
            Console.Error.WriteLine(Usage);
            return 2;
        }

        var result = Execute(options);
        try
        {
            var directory = Path.GetDirectoryName(Path.GetFullPath(options.ResultFile));
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
            File.WriteAllText(options.ResultFile, JsonSerializer.Serialize(result, ResultJsonOptions), new UTF8Encoding(false));
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"Could not write the result file ({exception.GetType().Name}).");
            return 1;
        }

        if (result.Status == "Succeeded")
        {
            Console.WriteLine($"Sunshine setup {result.Operation}: succeeded.");
            return 0;
        }

        Console.Error.WriteLine($"Sunshine setup {result.Operation}: failed ({result.FailureCategory}).");
        return 1;
    }

    private const string Usage = """
Usage:
  SunshineCredentialReset --provision-tls --sunshine-root <directory> --result-file <path> [--backup-root <directory>]
  SunshineCredentialReset --apply --sunshine-root <directory> --agent-data <directory> --username <name> --credential-note <path> --result-file <path> [--backup-root <directory>]

Both modes must run from an elevated Windows administrator account, and --apply requires
the installed SunshineService to be stopped.
""";

    private static Options ParseOptions(string[] args)
    {
        var modeApply = false;
        var modeTls = false;
        string? sunshineRoot = null;
        string? resultFile = null;
        string? agentData = null;
        string? userName = null;
        string? credentialNote = null;
        string? backupRoot = null;

        for (var index = 0; index < args.Length; index++)
        {
            switch (args[index])
            {
                case "--apply":
                    modeApply = true;
                    break;
                case "--provision-tls":
                    modeTls = true;
                    break;
                case "--sunshine-root":
                    sunshineRoot = NextValue(args, ref index);
                    break;
                case "--result-file":
                    resultFile = NextValue(args, ref index);
                    break;
                case "--agent-data":
                    agentData = NextValue(args, ref index);
                    break;
                case "--username":
                    userName = NextValue(args, ref index);
                    break;
                case "--credential-note":
                    credentialNote = NextValue(args, ref index);
                    break;
                case "--backup-root":
                    backupRoot = NextValue(args, ref index);
                    break;
                default:
                    throw new ArgumentException($"Unknown argument '{args[index]}'.");
            }
        }

        if (modeApply == modeTls)
            throw new ArgumentException("Specify exactly one of --apply or --provision-tls.");
        if (string.IsNullOrWhiteSpace(resultFile) || !Path.IsPathFullyQualified(resultFile))
            throw new ArgumentException("--result-file must be an absolute path.");
        if (string.IsNullOrWhiteSpace(sunshineRoot) || !Path.IsPathFullyQualified(sunshineRoot))
            throw new ArgumentException("--sunshine-root must be an absolute path.");
        if (!Directory.Exists(sunshineRoot))
            throw new ArgumentException("The Sunshine installation directory does not exist.");

        if (modeApply)
        {
            if (string.IsNullOrWhiteSpace(agentData) || !Path.IsPathFullyQualified(agentData))
                throw new ArgumentException("--agent-data must be an absolute path.");
            if (string.IsNullOrWhiteSpace(userName) || !IsValidUserName(userName))
                throw new ArgumentException("--username is not a valid Sunshine management user name.");
            if (string.IsNullOrWhiteSpace(credentialNote) || !Path.IsPathFullyQualified(credentialNote))
                throw new ArgumentException("--credential-note must be an absolute path.");
        }
        else if (agentData is not null || userName is not null || credentialNote is not null)
        {
            throw new ArgumentException("--agent-data, --username and --credential-note are only valid with --apply.");
        }

        if (backupRoot is not null && !Path.IsPathFullyQualified(backupRoot))
            throw new ArgumentException("--backup-root must be an absolute path.");

        return new(
            Apply: modeApply,
            ProvisionTls: modeTls,
            SunshineRoot: Path.GetFullPath(sunshineRoot),
            ResultFile: Path.GetFullPath(resultFile),
            AgentDataDirectory: agentData is null ? null : Path.GetFullPath(agentData),
            UserName: userName,
            CredentialNotePath: credentialNote is null ? null : Path.GetFullPath(credentialNote),
            BackupRoot: backupRoot is null ? null : Path.GetFullPath(backupRoot));
    }

    private static string NextValue(string[] args, ref int index)
    {
        if (index + 1 >= args.Length) throw new ArgumentException($"Missing value for '{args[index]}'.");
        return args[++index];
    }

    private static bool IsValidUserName(string userName) =>
        !string.IsNullOrWhiteSpace(userName) &&
        userName.Length <= 128 &&
        !userName.Contains(':') &&
        !userName.Any(char.IsControl);

    private static SetupResult Execute(Options options)
    {
        var operation = options.Apply ? "apply" : "provision-tls";
        string? backupDirectory = null;
        var rollback = new RollbackScope();
        try
        {
            EnsureElevated();
            var installation = ResolveInstallation(options.SunshineRoot);
            var serviceState = RequireSunshineServiceStopped();

            backupDirectory = CreateBackupDirectory(options, installation);
            var backups = new List<FileBackup>();
            foreach (var path in EnumerateBackupCandidates(options, installation))
                backups.Add(BackupFile(path, backupDirectory));
            rollback.Add(() => RestoreFiles(backups));

            var tls = EnsureManagementCertificate(installation, rollback);
            if (!options.Apply)
            {
                return new(
                    Operation: operation,
                    Status: "Succeeded",
                    SunshineRoot: installation.Root,
                    SunshineServiceState: serviceState,
                    BackupDirectory: backupDirectory,
                    TlsAction: tls.Action,
                    TlsThumbprint: tls.Thumbprint,
                    TrustedInLocalMachineRoot: tls.TrustedInLocalMachineRoot,
                    TrustedInCurrentUserRoot: tls.TrustedInCurrentUserRoot);
            }

            var credentials = ApplyCredentials(options, installation, backupDirectory, rollback);
            WriteCredentialNote(options.CredentialNotePath!, credentials.Username, credentials.Password);
            rollback.Add(() => { DeleteFileIfExists(options.CredentialNotePath!); return null; });

            return new(
                Operation: operation,
                Status: "Succeeded",
                SunshineRoot: installation.Root,
                SunshineServiceState: serviceState,
                BackupDirectory: backupDirectory,
                TlsAction: tls.Action,
                TlsThumbprint: tls.Thumbprint,
                TrustedInLocalMachineRoot: tls.TrustedInLocalMachineRoot,
                TrustedInCurrentUserRoot: tls.TrustedInCurrentUserRoot,
                CredentialStateVerification: credentials.StateVerification,
                Username: credentials.Username,
                PairingStatePreserved: credentials.PairingStatePreserved,
                AgentStoreWritten: true,
                AgentStoreVerified: credentials.AgentStoreVerified,
                CredentialNotePath: options.CredentialNotePath,
                SunshineCommandExitCode: credentials.CommandExitCode);
        }
        catch (Exception exception)
        {
            var rollbackStatus = rollback.Rollback();
            return new(
                Operation: operation,
                Status: "Failed",
                FailureCategory: exception.GetType().Name,
                FailureDetail: exception.Message,
                SunshineRoot: options.SunshineRoot,
                BackupDirectory: backupDirectory,
                RollbackStatus: rollbackStatus);
        }
    }

    private static void EnsureElevated()
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("Sunshine setup requires Windows.");
        using var identity = WindowsIdentity.GetCurrent();
        if (identity.User is null || !new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator))
            throw new UnauthorizedAccessException("Run this tool from an elevated administrator account.");
    }

    private sealed record Installation(string Root, string ConfigDirectory, string CredentialsDirectory, string SunshineExecutable, string StateFile);

    private static Installation ResolveInstallation(string sunshineRoot)
    {
        var root = Path.GetFullPath(sunshineRoot).TrimEnd(Path.DirectorySeparatorChar);
        var executable = Path.Combine(root, "Sunshine.exe");
        if (!File.Exists(executable))
            executable = Path.Combine(root, "sunshine.exe");
        if (!File.Exists(executable))
            throw new InvalidOperationException("Sunshine.exe was not found in the Sunshine installation directory.");

        var configDirectory = Path.Combine(root, "config");
        if (!Directory.Exists(configDirectory))
            throw new InvalidOperationException("The Sunshine configuration directory does not exist.");
        var credentialsDirectory = Path.Combine(configDirectory, "credentials");
        var stateFile = Path.Combine(configDirectory, "sunshine_state.json");
        if (!File.Exists(stateFile))
            throw new InvalidOperationException("sunshine_state.json was not found; Sunshine has not been initialized.");

        return new(root, configDirectory, credentialsDirectory, executable, stateFile);
    }

    private static string RequireSunshineServiceStopped()
    {
        using var service = new ServiceController(SunshineServiceName);
        var state = service.Status.ToString();
        if (state != nameof(ServiceControllerStatus.Stopped))
            throw new InvalidOperationException($"The {SunshineServiceName} service must be stopped before setup; it is {state}.");
        return state;
    }

    private static string CreateBackupDirectory(Options options, Installation installation)
    {
        var root = options.BackupRoot ?? Path.Combine(installation.ConfigDirectory, "setup-backup-" + DateTime.UtcNow.ToString("yyyyMMddTHHmmssZ", System.Globalization.CultureInfo.InvariantCulture));
        Directory.CreateDirectory(root);
        ProtectDirectoryToAdministrators(root);
        return root;
    }

    private static IEnumerable<string> EnumerateBackupCandidates(Options options, Installation installation)
    {
        yield return installation.StateFile;
        yield return Path.Combine(installation.CredentialsDirectory, "cacert.pem");
        yield return Path.Combine(installation.CredentialsDirectory, "cakey.pem");
        if (options.AgentDataDirectory is not null)
            yield return AgentStorePath(options.AgentDataDirectory);
    }

    private static string AgentStorePath(string agentDataDirectory) =>
        Path.Combine(agentDataDirectory, WindowsSunshineCredentialStore.CredentialDirectoryName, WindowsSunshineCredentialStore.CredentialFileName);

    private sealed record FileBackup(string OriginalPath, string? BackupPath, bool Existed);

    private static FileBackup BackupFile(string path, string backupDirectory)
    {
        if (!File.Exists(path))
            return new(path, null, false);

        var backupPath = Path.Combine(backupDirectory, Guid.NewGuid().ToString("N") + "-" + Path.GetFileName(path));
        File.Copy(path, backupPath, overwrite: false);
        return new(path, backupPath, true);
    }

    private static string? RestoreFiles(IEnumerable<FileBackup> backups)
    {
        var failures = new List<string>();
        foreach (var backup in backups.Reverse())
        {
            try
            {
                if (backup.Existed)
                {
                    var directory = Path.GetDirectoryName(backup.OriginalPath);
                    if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
                    File.Copy(backup.BackupPath!, backup.OriginalPath, overwrite: true);
                }
                else if (File.Exists(backup.OriginalPath))
                {
                    File.Delete(backup.OriginalPath);
                }
            }
            catch (Exception exception)
            {
                failures.Add($"{Path.GetFileName(backup.OriginalPath)}: {exception.GetType().Name}");
            }
        }

        return failures.Count == 0 ? null : "Restore failures: " + string.Join("; ", failures);
    }

    private sealed record TlsOutcome(
        string Action,
        string Thumbprint,
        bool TrustedInLocalMachineRoot,
        bool TrustedInCurrentUserRoot);

    private static TlsOutcome EnsureManagementCertificate(Installation installation, RollbackScope rollback)
    {
        var certPath = Path.Combine(installation.CredentialsDirectory, "cacert.pem");
        var keyPath = Path.Combine(installation.CredentialsDirectory, "cakey.pem");

        var existing = TryLoadCertificate(certPath);
        var action = "Reused";
        if (existing is null || !KeyMatchesCertificate(keyPath, existing) || !IsLocalhostServerCertificate(existing))
        {
            using var generated = CreateLocalhostCertificate();
            WriteCertificateAndKey(certPath, keyPath, generated);
            existing?.Dispose();
            existing = X509CertificateLoader.LoadCertificateFromFile(certPath);
            action = "Provisioned";
        }

        try
        {
            EnsureSystemReadable(certPath);
            EnsureSystemReadable(keyPath);

            var trustedInLocalMachine = EnsureTrusted(StoreLocation.LocalMachine, existing, rollback);
            var trustedInCurrentUser = EnsureTrusted(StoreLocation.CurrentUser, existing, rollback);
            VerifyChain(existing);
            RequireTrusted(StoreLocation.LocalMachine, existing);
            RequireTrusted(StoreLocation.CurrentUser, existing);

            return new(action, existing.Thumbprint, trustedInLocalMachine, trustedInCurrentUser);
        }
        finally
        {
            existing.Dispose();
        }
    }

    private static X509Certificate2? TryLoadCertificate(string path)
    {
        if (!File.Exists(path)) return null;
        try
        {
            return X509CertificateLoader.LoadCertificateFromFile(path);
        }
        catch (CryptographicException)
        {
            return null;
        }
    }

    private static bool KeyMatchesCertificate(string keyPath, X509Certificate2 certificate)
    {
        if (!File.Exists(keyPath)) return false;
        try
        {
            using var rsa = RSA.Create();
            rsa.ImportFromPem(File.ReadAllText(keyPath));
            using var certificateKey = certificate.GetRSAPublicKey();
            if (certificateKey is null) return false;
            var keyParameters = rsa.ExportParameters(includePrivateParameters: false);
            var certificateParameters = certificateKey.ExportParameters(includePrivateParameters: false);
            return keyParameters.Modulus!.SequenceEqual(certificateParameters.Modulus!) &&
                keyParameters.Exponent!.SequenceEqual(certificateParameters.Exponent!);
        }
        catch (CryptographicException)
        {
            return false;
        }
    }

    private static bool IsLocalhostServerCertificate(X509Certificate2 certificate)
    {
        if (!certificate.SubjectName.RawData.SequenceEqual(certificate.IssuerName.RawData))
            return false;
        var now = DateTimeOffset.UtcNow;
        if (certificate.NotBefore.ToUniversalTime() > now.UtcDateTime.Add(CertificateClockSkew) ||
            certificate.NotAfter.ToUniversalTime() < now.UtcDateTime.Add(MinimumCertificateRemaining))
            return false;

        var alternativeNames = certificate.Extensions
            .Where(extension => extension.Oid?.Value == "2.5.29.17")
            .Select(extension => TryReadSubjectAlternativeNames(extension.RawData))
            .FirstOrDefault(names => names is not null);
        if (alternativeNames is null)
            return false;

        var hasHostName = alternativeNames.HostNames.Any(name =>
            LocalHostNames.Contains(name, StringComparer.OrdinalIgnoreCase));
        var hasLoopbackAddress = alternativeNames.LoopbackAddress;
        if (!hasHostName || !hasLoopbackAddress)
            return false;

        var constraints = certificate.Extensions.OfType<X509BasicConstraintsExtension>().FirstOrDefault();
        return constraints is null || constraints.CertificateAuthority;
    }

    private sealed record SubjectAlternativeNames(IReadOnlyList<string> HostNames, bool LoopbackAddress);

    private static SubjectAlternativeNames? TryReadSubjectAlternativeNames(byte[] rawData)
    {
        try
        {
            var hostNames = new List<string>();
            var loopbackAddress = false;
            var reader = new AsnReader(rawData, AsnEncodingRules.DER).ReadSequence();
            while (reader.HasData)
            {
                var tag = reader.PeekTag();
                if (tag.HasSameClassAndValue(new Asn1Tag(TagClass.ContextSpecific, 2)))
                {
                    hostNames.Add(reader.ReadCharacterString(UniversalTagNumber.IA5String, new Asn1Tag(TagClass.ContextSpecific, 2)));
                }
                else if (tag.HasSameClassAndValue(new Asn1Tag(TagClass.ContextSpecific, 7)))
                {
                    var addressBytes = reader.ReadOctetString(new Asn1Tag(TagClass.ContextSpecific, 7));
                    if (addressBytes.Length is 4 or 16 && IPAddress.IsLoopback(new IPAddress(addressBytes)))
                        loopbackAddress = true;
                }
                else
                {
                    reader.ReadEncodedValue();
                }
            }

            return new(hostNames, loopbackAddress);
        }
        catch (AsnContentException)
        {
            return null;
        }
    }

    private static X509Certificate2 CreateLocalhostCertificate()
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest(
            new X500DistinguishedName("CN=localhost"),
            rsa,
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(
            certificateAuthority: true,
            hasPathLengthConstraint: false,
            pathLengthConstraint: 0,
            critical: true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(
            X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment | X509KeyUsageFlags.KeyCertSign,
            critical: true));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(
            new OidCollection { new("1.3.6.1.5.5.7.3.1") },
            critical: false));
        var alternativeNames = new SubjectAlternativeNameBuilder();
        alternativeNames.AddDnsName("localhost");
        alternativeNames.AddIpAddress(IPAddress.Loopback);
        alternativeNames.AddIpAddress(IPAddress.IPv6Loopback);
        request.CertificateExtensions.Add(alternativeNames.Build());

        var certificate = request.CreateSelfSigned(
            DateTimeOffset.UtcNow - CertificateClockSkew,
            DateTimeOffset.UtcNow.AddYears(CertificateLifetimeYears));
        return certificate;
    }

    private static void WriteCertificateAndKey(string certPath, string keyPath, X509Certificate2 certificate)
    {
        var directory = Path.GetDirectoryName(certPath) ?? throw new InvalidOperationException("The certificate path has no directory.");
        Directory.CreateDirectory(directory);

        using var rsa = certificate.GetRSAPrivateKey()
            ?? throw new CryptographicException("The generated certificate has no RSA private key.");
        var keyPem = rsa.ExportPkcs8PrivateKeyPem();
        var certPem = certificate.ExportCertificatePem();
        try
        {
            WriteFileReplacing(keyPath, keyPem);
            WriteFileReplacing(certPath, certPem);
        }
        finally
        {
        }
    }

    private static void WriteFileReplacing(string path, string content)
    {
        var temporaryPath = path + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            using (var output = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            using (var writer = new StreamWriter(output, new UTF8Encoding(false)))
            {
                writer.Write(content);
                writer.Flush();
                output.Flush(flushToDisk: true);
            }
            File.Move(temporaryPath, path, overwrite: true);
            temporaryPath = string.Empty;
        }
        finally
        {
            if (!string.IsNullOrEmpty(temporaryPath) && File.Exists(temporaryPath))
                File.Delete(temporaryPath);
        }
    }

    private static void EnsureSystemReadable(string path)
    {
        var systemSid = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
        var security = new FileInfo(path).GetAccessControl(AccessControlSections.Access);
        var readable = false;
        foreach (FileSystemAccessRule rule in security.GetAccessRules(includeExplicit: true, includeInherited: true, targetType: typeof(SecurityIdentifier)))
        {
            if (rule.AccessControlType != AccessControlType.Allow || rule.IdentityReference is not SecurityIdentifier sid || sid != systemSid)
                continue;
            if ((rule.FileSystemRights & (FileSystemRights.Read | FileSystemRights.ReadData)) != 0)
                readable = true;
        }

        if (!readable)
        {
            security.AddAccessRule(new FileSystemAccessRule(systemSid, FileSystemRights.Read, AccessControlType.Allow));
            new FileInfo(path).SetAccessControl(security);
        }
    }

    // Ensures the certificate is present in the given Root store. Returns true when the
    // store trusts the certificate after the call (whether it was added now or before).
    private static bool EnsureTrusted(StoreLocation location, X509Certificate2 certificate, RollbackScope rollback)
    {
        using var store = new X509Store(StoreName.Root, location);
        store.Open(OpenFlags.ReadWrite);
        if (store.Certificates.Find(X509FindType.FindByThumbprint, certificate.Thumbprint, validOnly: false).Count > 0)
            return true;

        var thumbprint = certificate.Thumbprint;
        using var publicOnly = X509CertificateLoader.LoadCertificate(certificate.RawData);
        store.Add(publicOnly);
        rollback.Add(() => RemoveFromStore(location, thumbprint));
        return true;
    }

    private static void RequireTrusted(StoreLocation location, X509Certificate2 certificate)
    {
        using var store = new X509Store(StoreName.Root, location);
        store.Open(OpenFlags.ReadOnly);
        if (store.Certificates.Find(X509FindType.FindByThumbprint, certificate.Thumbprint, validOnly: false).Count == 0)
            throw new CryptographicException($"The Sunshine management certificate is not trusted in the {location} Root store.");
    }

    private static string? RemoveFromStore(StoreLocation location, string thumbprint)
    {
        try
        {
            using var store = new X509Store(StoreName.Root, location);
            store.Open(OpenFlags.ReadWrite);
            foreach (var certificate in store.Certificates.Find(X509FindType.FindByThumbprint, thumbprint, validOnly: false))
                store.Remove(certificate);
            return null;
        }
        catch (Exception exception)
        {
            return $"{location} trust removal: {exception.GetType().Name}";
        }
    }

    private static void VerifyChain(X509Certificate2 certificate)
    {
        using var chain = new X509Chain();
        chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
        chain.ChainPolicy.VerificationFlags = X509VerificationFlags.NoFlag;
        if (!chain.Build(certificate))
        {
            var statuses = string.Join(",", chain.ChainStatus.Select(status => status.Status.ToString()));
            throw new CryptographicException($"The provisioned TLS certificate is not trusted by Windows ({statuses}).");
        }
    }

    private sealed record CredentialOutcome(
        string Username,
        string Password,
        string StateVerification,
        bool PairingStatePreserved,
        bool AgentStoreVerified,
        int? CommandExitCode);

    private static CredentialOutcome ApplyCredentials(Options options, Installation installation, string backupDirectory, RollbackScope rollback)
    {
        var stateBefore = ReadState(installation.StateFile);
        var username = options.UserName!;
        var password = GeneratePassword();

        var (exitCode, _) = RunSunshineCredentialCommand(installation, username, password);
        var stateAfter = ReadState(installation.StateFile);
        VerifyCredentialState(stateAfter, username, password);
        var pairingPreserved = JsonNode.DeepEquals(stateBefore["root"], stateAfter["root"]) ||
            (stateBefore["root"] is null && stateAfter["root"] is null);
        if (!pairingPreserved)
            throw new InvalidOperationException("The Sunshine pairing state changed during the credential reset.");

        var store = new WindowsSunshineCredentialStore(
            options.AgentDataDirectory!,
            WindowsSunshineCredentialStore.ResolveAgentServiceSid(),
            new WindowsDpapiMachineSecretProtector());
        var agentStoreBackup = BackupFile(store.CredentialPath, backupDirectory);
        rollback.Add(() => RestoreFiles([agentStoreBackup]));

        store.Save(new SunshineManagementCredential(username, password), replace: true);
        var loaded = store.Load() ?? throw new InvalidDataException("The Agent credential store is unreadable after writing.");
        if (!string.Equals(loaded.UserName, username, StringComparison.Ordinal) ||
            !string.Equals(loaded.Password, password, StringComparison.Ordinal))
            throw new InvalidDataException("The Agent credential store does not contain the new credentials.");

        VerifyStoreAcl(store);
        return new(username, password, "SunshineStateHashMatches", pairingPreserved, true, exitCode);
    }

    private static JsonObject ReadState(string path)
    {
        var text = File.ReadAllText(path);
        if (text.Length is 0 or > 64 * 1024)
            throw new InvalidDataException("sunshine_state.json has an unexpected size.");
        var state = JsonNode.Parse(text) as JsonObject
            ?? throw new InvalidDataException("sunshine_state.json is not a JSON object.");
        foreach (var required in new[] { "username", "salt", "password" })
        {
            if (state[required] is not JsonValue value || value.GetValueKind() != JsonValueKind.String)
                throw new InvalidDataException($"sunshine_state.json is missing the '{required}' value.");
        }
        return state;
    }

    private static void VerifyCredentialState(JsonObject state, string username, string password)
    {
        var storedUser = state["username"]!.GetValue<string>();
        if (!string.Equals(storedUser, username, StringComparison.Ordinal))
            throw new InvalidDataException("Sunshine did not store the requested management user name.");

        var salt = state["salt"]!.GetValue<string>();
        var storedHash = state["password"]!.GetValue<string>();
        if (salt.Length is < 1 or > 128 || storedHash.Length != 64)
            throw new InvalidDataException("Sunshine stored an unexpected credential hash format.");

        // Foundation Sunshine writes util::hex(crypto::hash(password + salt)).to_string().
        // util::hex() walks the digest backwards by default, so the stored string is the
        // SHA-256 digest in reverse byte order and uppercase hex.
        var expected = ComputeSunshineCredentialHash(password, salt);
        if (!string.Equals(expected, storedHash, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The stored Sunshine password hash does not match the new credential.");
    }

    internal static string ComputeSunshineCredentialHash(string password, string salt)
    {
        var digest = SHA256.HashData(Encoding.UTF8.GetBytes(password + salt));
        Array.Reverse(digest);
        return Convert.ToHexString(digest);
    }

    private static void VerifyStoreAcl(WindowsSunshineCredentialStore store)
    {
        foreach (var path in new[] { store.DirectoryPath, store.CredentialPath })
        {
            if (!File.Exists(path) && !Directory.Exists(path))
                throw new InvalidDataException($"The Agent credential store path '{Path.GetFileName(path)}' is missing.");
            var security = Directory.Exists(path)
                ? (FileSystemSecurity)new DirectoryInfo(path).GetAccessControl(AccessControlSections.Access | AccessControlSections.Owner)
                : new FileInfo(path).GetAccessControl(AccessControlSections.Access | AccessControlSections.Owner);
            if (!security.AreAccessRulesProtected)
                throw new UnauthorizedAccessException("The Agent credential store ACL inherits access rules.");
        }
    }

    private static (int? ExitCode, string CommandOutput) RunSunshineCredentialCommand(
        Installation installation,
        string username,
        string password)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = installation.SunshineExecutable,
            WorkingDirectory = installation.Root,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        startInfo.ArgumentList.Add("--creds");
        startInfo.ArgumentList.Add(username);
        startInfo.ArgumentList.Add(password);

        using var process = new Process { StartInfo = startInfo };
        if (!process.Start())
            throw new InvalidOperationException("The Sunshine credential command could not be started.");

        var standardOutput = process.StandardOutput.ReadToEndAsync();
        var standardError = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit((int)SunshineCommandTimeout.TotalMilliseconds))
        {
            try { process.Kill(entireProcessTree: true); } catch { }
            throw new System.TimeoutException("The Sunshine credential command timed out.");
        }

        var output = string.Concat(standardOutput.GetAwaiter().GetResult(), standardError.GetAwaiter().GetResult());
        // Only the beginning of the diagnostic output is kept; it never contains the password
        // because save_user_creds logs the user name only.
        if (output.Length > 512) output = output[..512];
        return (process.ExitCode, output);
    }

    private static string GeneratePassword()
    {
        var buffer = new char[PasswordLength];
        for (var index = 0; index < buffer.Length; index++)
            buffer[index] = PasswordAlphabet[RandomNumberGenerator.GetInt32(PasswordAlphabet.Length)];
        return new string(buffer);
    }

    private static void WriteCredentialNote(string path, string username, string password)
    {
        var directory = Path.GetDirectoryName(path) ?? throw new InvalidOperationException("The credential note path has no directory.");
        Directory.CreateDirectory(directory);

        var content = $"""
Sunshine Web manager credentials (generated by Reset-SunshineCredentials.ps1)

Generated (local time): {DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm:ss zzz", System.Globalization.CultureInfo.InvariantCulture)}
URL: https://localhost:47990
Username: {username}
Password: {password}

The Sunshine web UI is reachable only while a fenced maintenance window is open
(the Stream Guard stops Sunshine whenever no stream authority is active).
Keep this file out of source control and delete it after storing the password
in a password manager.
""";
        if (Encoding.UTF8.GetByteCount(content) > MaximumCredentialNoteBytes)
            throw new InvalidDataException("The credential note is unexpectedly large.");

        var temporaryPath = path + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            File.WriteAllText(temporaryPath, content, new UTF8Encoding(false));
            RestrictCredentialNoteToCurrentUser(temporaryPath);
            File.Move(temporaryPath, path, overwrite: true);
            temporaryPath = string.Empty;
        }
        finally
        {
            if (!string.IsNullOrEmpty(temporaryPath) && File.Exists(temporaryPath))
                File.Delete(temporaryPath);
        }
    }

    private static void RestrictCredentialNoteToCurrentUser(string path)
    {
        using var identity = WindowsIdentity.GetCurrent();
        var userSid = identity.User ?? throw new UnauthorizedAccessException("The current Windows identity has no user SID.");
        var systemSid = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
        var administratorsSid = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);

        var security = new FileSecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.SetOwner(userSid);
        foreach (var sid in new[] { userSid, systemSid, administratorsSid }.Distinct())
            security.AddAccessRule(new FileSystemAccessRule(sid, FileSystemRights.FullControl, AccessControlType.Allow));
        new FileInfo(path).SetAccessControl(security);
    }

    private static void ProtectDirectoryToAdministrators(string path)
    {
        var systemSid = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
        var administratorsSid = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
        var security = new DirectorySecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.SetOwner(administratorsSid);
        foreach (var sid in new[] { systemSid, administratorsSid })
        {
            security.AddAccessRule(new FileSystemAccessRule(
                sid,
                FileSystemRights.FullControl,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
                PropagationFlags.None,
                AccessControlType.Allow));
        }
        new DirectoryInfo(path).SetAccessControl(security);
    }

    private static void DeleteFileIfExists(string path)
    {
        if (File.Exists(path)) File.Delete(path);
    }


    private sealed class RollbackScope
    {
        private readonly List<Func<string?>> steps = [];

        public void Add(Func<string?> step) => steps.Add(step);

        public string? Rollback()
        {
            var failures = new List<string>();
            for (var index = steps.Count - 1; index >= 0; index--)
            {
                try
                {
                    var failure = steps[index]();
                    if (!string.IsNullOrEmpty(failure)) failures.Add(failure);
                }
                catch (Exception exception)
                {
                    failures.Add(exception.GetType().Name);
                }
            }

            steps.Clear();
            return failures.Count == 0 ? null : "Rollback failures: " + string.Join("; ", failures);
        }
    }
}

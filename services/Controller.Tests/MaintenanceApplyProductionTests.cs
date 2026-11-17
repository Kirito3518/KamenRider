using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using KamenRaider.AgentMaintenance;

namespace Controller.Tests;

[SupportedOSPlatform("windows")]
public sealed class MaintenanceApplyProductionTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "kr-apply-prod-" + Guid.NewGuid().ToString("N"));

    public MaintenanceApplyProductionTests() => Directory.CreateDirectory(root);

    public void Dispose()
    {
        try { Directory.Delete(root, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private static ushort NativeMachine => RuntimeInformation.OSArchitecture switch
    {
        Architecture.X64 => 0x8664,
        Architecture.Arm64 => 0xAA64,
        _ => throw new PlatformNotSupportedException()
    };

    private static ushort ForeignMachine => NativeMachine == 0x8664 ? (ushort)0xAA64 : (ushort)0x8664;

    private static void WritePeFile(string path, ushort machine, long length)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var bytes = new byte[length];
        bytes[0] = (byte)'M';
        bytes[1] = (byte)'Z';
        BitConverter.GetBytes(0x80).CopyTo(bytes, 0x3C);
        bytes[0x80] = (byte)'P';
        bytes[0x81] = (byte)'E';
        BitConverter.GetBytes(machine).CopyTo(bytes, 0x84);
        File.WriteAllBytes(path, bytes);
    }

    private string CreateSunshineTree(ushort machine = 0, long length = 0)
    {
        var sunshineDirectory = Path.Combine(root, "Sunshine");
        var path = Path.Combine(sunshineDirectory, "Sunshine.exe");
        WritePeFile(path, machine == 0 ? NativeMachine : machine,
            length == 0 ? WindowsMaintenanceTrustedSunshinePathProvider.MinimumExecutableBytes + 0x100 : length);
        return path;
    }

    [Fact]
    public void TrustedSunshineProviderAcceptsACanonicalNativeExecutable()
    {
        if (!OperatingSystem.IsWindows()) return;
        var path = CreateSunshineTree();
        var provider = new WindowsMaintenanceTrustedSunshinePathProvider(path, enforceTrust: false);

        var resolved = provider.GetTrustedSunshineExecutablePath(new MaintenanceCommandRequest(
            1, "apply", "op-1", "1.0.0", "win-x64", new string('a', 64), 1024, "chain-1", "op-1", 1));

        Assert.Equal(Path.GetFullPath(path), resolved);
    }

    [Fact]
    public void TrustedSunshineProviderRejectsForeignArchitecture()
    {
        if (!OperatingSystem.IsWindows()) return;
        var path = CreateSunshineTree(ForeignMachine);
        var provider = new WindowsMaintenanceTrustedSunshinePathProvider(path, enforceTrust: false);

        Assert.Throws<InvalidDataException>(() => provider.GetTrustedSunshineExecutablePath(
            new MaintenanceCommandRequest(1, "apply", "op-1", "1.0.0", "win-x64", new string('a', 64), 1024, "chain-1", "op-1", 1)));
    }

    [Fact]
    public void TrustedSunshineProviderRejectsMissingOversizedOrMisnamedExecutables()
    {
        if (!OperatingSystem.IsWindows()) return;
        var request = new MaintenanceCommandRequest(
            1, "apply", "op-1", "1.0.0", "win-x64", new string('a', 64), 1024, "chain-1", "op-1", 1);

        Directory.CreateDirectory(Path.Combine(root, "missing-sunshine", "Sunshine"));
        var missing = new WindowsMaintenanceTrustedSunshinePathProvider(
            Path.Combine(root, "missing-sunshine", "Sunshine", "Sunshine.exe"), enforceTrust: false);
        Assert.Throws<FileNotFoundException>(() => missing.GetTrustedSunshineExecutablePath(request));

        var tinyPath = Path.Combine(root, "tiny", "Sunshine", "Sunshine.exe");
        WritePeFile(tinyPath, NativeMachine, 4096);
        var tiny = new WindowsMaintenanceTrustedSunshinePathProvider(tinyPath, enforceTrust: false);
        Assert.Throws<InvalidDataException>(() => tiny.GetTrustedSunshineExecutablePath(request));

        var misnamedPath = Path.Combine(root, "misnamed", "Sunshine", "NotSunshine.exe");
        WritePeFile(misnamedPath, NativeMachine, WindowsMaintenanceTrustedSunshinePathProvider.MinimumExecutableBytes + 0x100);
        var misnamed = new WindowsMaintenanceTrustedSunshinePathProvider(misnamedPath, enforceTrust: false);
        Assert.Throws<InvalidDataException>(() => misnamed.GetTrustedSunshineExecutablePath(request));
    }

    [Fact]
    public void TrustedSunshineProviderRejectsAPathOutsideTheCanonicalProgramFilesRoot()
    {
        if (!OperatingSystem.IsWindows()) return;
        var path = CreateSunshineTree();
        var provider = new WindowsMaintenanceTrustedSunshinePathProvider(
            path, enforceTrust: false, requiredProgramFilesRoot: Path.Combine(root, "other-program-files"));

        Assert.Throws<InvalidDataException>(() => provider.GetTrustedSunshineExecutablePath(
            new MaintenanceCommandRequest(1, "apply", "op-1", "1.0.0", "win-x64", new string('a', 64), 1024, "chain-1", "op-1", 1)));
    }

    [Fact]
    public void TrustedSunshineProviderValidatesTheInstalledSunshineBinaryWhenPresent()
    {
        if (!OperatingSystem.IsWindows()) return;
        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        var installed = Path.Combine(programFiles, "Sunshine", "Sunshine.exe");
        if (!File.Exists(installed)) return;

        var provider = new WindowsMaintenanceTrustedSunshinePathProvider();
        var resolved = provider.GetTrustedSunshineExecutablePath(new MaintenanceCommandRequest(
            1, "apply", "op-1", "1.0.0", "win-x64", new string('a', 64), 1024, "chain-1", "op-1", 1));

        Assert.True(WindowsMaintenanceTrustedSunshinePathProvider.PathEquals(installed, resolved));
    }

    [Fact]
    public void PeMachineParserRejectsNonPeFiles()
    {
        if (!OperatingSystem.IsWindows()) return;
        var path = Path.Combine(root, "notpe.bin");
        File.WriteAllBytes(path, new byte[128]);
        Assert.Throws<InvalidDataException>(() => WindowsMaintenanceTrustedSunshinePathProvider.ReadPeMachine(path));

        var badSignature = Path.Combine(root, "bad-signature.bin");
        var bytes = new byte[256];
        bytes[0] = (byte)'M';
        bytes[1] = (byte)'Z';
        BitConverter.GetBytes(0x80).CopyTo(bytes, 0x3C);
        File.WriteAllBytes(badSignature, bytes);
        Assert.Throws<InvalidDataException>(() => WindowsMaintenanceTrustedSunshinePathProvider.ReadPeMachine(badSignature));
    }

    [Fact]
    public void ApplyFactoryDerivesTheFixedServiceLayout()
    {
        if (!OperatingSystem.IsWindows()) return;
        var installRoot = Path.Combine(root, "Program Files", "KamenRaider", "Agent");
        Directory.CreateDirectory(installRoot);
        File.WriteAllText(Path.Combine(installRoot, "Agent.exe"), "fixture");
        var sunshine = CreateSunshineTree();
        var service = Path.Combine(Path.GetDirectoryName(sunshine)!, "tools", "sunshinesvc.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(service)!);
        File.WriteAllText(service, "fixture");

        var layout = WindowsMaintenanceApplyOperationFactory.CreateServiceLayout(installRoot, sunshine);

        Assert.True(WindowsMaintenanceTrustedSunshinePathProvider.PathEquals(
            Path.Combine(installRoot, "Agent.exe"), layout.AgentExecutablePath));
        Assert.True(WindowsMaintenanceTrustedSunshinePathProvider.PathEquals(service, layout.SunshineServiceExecutablePath));
        Assert.True(WindowsMaintenanceTrustedSunshinePathProvider.PathEquals(sunshine, layout.SunshineExecutablePath));
    }

    [Fact]
    public void ApplyFactoryRejectsUnsupportedLayouts()
    {
        if (!OperatingSystem.IsWindows()) return;
        var sunshine = CreateSunshineTree();
        var installRoot = Path.Combine(root, "Program Files", "KamenRaider", "Agent");
        Directory.CreateDirectory(installRoot);

        Assert.Throws<DirectoryNotFoundException>(() =>
            WindowsMaintenanceApplyOperationFactory.CreateServiceLayout(installRoot, sunshine));

        var tools = Path.Combine(Path.GetDirectoryName(sunshine)!, "tools");
        Directory.CreateDirectory(tools);
        File.WriteAllText(Path.Combine(tools, "sunshinesvc.exe"), "fixture");
        Assert.Throws<InvalidDataException>(() =>
            WindowsMaintenanceApplyOperationFactory.CreateServiceLayout(Path.Combine(root, "elsewhere"), sunshine));
        Assert.Throws<InvalidDataException>(() =>
            WindowsMaintenanceApplyOperationFactory.CreateServiceLayout(installRoot, Path.Combine(root, "Sunshine", "Other.exe")));
    }

    [Fact]
    public void ApplyFactoryRejectsARequestWithoutChainIdentityBeforeAnyMutableWork()
    {
        if (!OperatingSystem.IsWindows()) return;
        var factory = new WindowsMaintenanceApplyOperationFactory(
            new WindowsMaintenanceBackupSecurity(),
            new InstalledAgentSourceInspector(
                new WindowsMaintenanceBackupSecurity().SourceRoot, new WindowsMaintenanceBackupSecurity()),
            new WindowsMaintenanceJournalStore(),
            new LocalUpdateEngine(new WindowsMaintenanceWorkStore()));
        var request = new MaintenanceCommandRequest(
            1, "apply", "op-1", "1.0.0", "win-x64", new string('a', 64), 1024);
        var authorization = new NoOpAuthorization();

        Assert.Throws<InvalidOperationException>(() => factory.Create(
            request, Path.Combine(root, "extract"), Path.Combine(root, "Sunshine", "Sunshine.exe"), authorization));
    }

    [Fact]
    public void HeldBackupAuthorizationDelegatesToTheApplyAuthorization()
    {
        if (!OperatingSystem.IsWindows()) return;
        var authorization = new NoOpAuthorization();
        var adapter = new HeldMaintenanceBackupAuthorization(authorization);

        adapter.VerifyHeld("op-1");
        adapter.VerifyHeldAndQuiesced("op-1");

        Assert.Equal(2, authorization.HeldCalls + authorization.HeldAndQuiescedCalls);
        Assert.Equal(1, authorization.HeldCalls);
        Assert.Equal(1, authorization.HeldAndQuiescedCalls);
    }

    private sealed class NoOpAuthorization : IMaintenanceApplyAuthorization
    {
        public int HeldCalls { get; private set; }
        public int HeldAndQuiescedCalls { get; private set; }

        public void VerifyHeld(string operationId) => HeldCalls++;

        public void VerifyHeldAndQuiesced(string operationId) => HeldAndQuiescedCalls++;
    }
}

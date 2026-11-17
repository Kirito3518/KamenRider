using System.Runtime.Versioning;
using System.Security.Principal;
using System.Text.Json;
using KamenRaider.Agent.Sunshine;

namespace Agent.Tests;

public class SunshineAppliedCredentialStoreTests
{
    [Fact]
    [SupportedOSPlatform("windows")]
    public void MissingRevisionFileReadsAsZero()
    {
        if (!OperatingSystem.IsWindows() || !WindowsAclProbe.CanSetProtectedAcl) return;
        using var directory = new TempDirectory();
        var store = NewStore(directory.Path);

        Assert.Equal(0, store.Read());
        Assert.False(Directory.Exists(store.DirectoryPath));
    }

    [Fact]
    [SupportedOSPlatform("windows")]
    public void RevisionFileRoundTripsAtomicallyAndContainsOnlyTheRevision()
    {
        if (!OperatingSystem.IsWindows() || !WindowsAclProbe.CanSetProtectedAcl) return;
        using var directory = new TempDirectory();
        var store = NewStore(directory.Path);

        store.Save(7);

        Assert.Equal(7, store.Read());
        Assert.Equal("{\"revision\":7}", File.ReadAllText(store.FilePath));
        Assert.True(new FileInfo(store.FilePath).GetAccessControl().AreAccessRulesProtected);
        Assert.True(new DirectoryInfo(store.DirectoryPath).GetAccessControl().AreAccessRulesProtected);
        Assert.Single(Directory.GetFiles(store.DirectoryPath));
    }

    [Fact]
    [SupportedOSPlatform("windows")]
    public void RevisionCanBeAdvancedByReplacingTheProtectedFile()
    {
        if (!OperatingSystem.IsWindows() || !WindowsAclProbe.CanSetProtectedAcl) return;
        using var directory = new TempDirectory();
        var store = NewStore(directory.Path);
        store.Save(3);

        store.Save(11);

        Assert.Equal(11, store.Read());
        Assert.Single(Directory.GetFiles(store.DirectoryPath));
    }

    [Theory]
    [InlineData(0L)]
    [InlineData(-1L)]
    [SupportedOSPlatform("windows")]
    public void NonPositiveRevisionIsRejected(long revision)
    {
        if (!OperatingSystem.IsWindows() || !WindowsAclProbe.CanSetProtectedAcl) return;
        using var directory = new TempDirectory();
        var store = NewStore(directory.Path);

        Assert.Throws<ArgumentOutOfRangeException>(() => store.Save(revision));
        Assert.False(File.Exists(store.FilePath));
    }

    [Theory]
    [InlineData("{bad json")]
    [InlineData("{\"revision\":0}")]
    [InlineData("{}")]
    [SupportedOSPlatform("windows")]
    public void TamperedRevisionFileIsRejected(string contents)
    {
        if (!OperatingSystem.IsWindows() || !WindowsAclProbe.CanSetProtectedAcl) return;
        using var directory = new TempDirectory();
        var store = NewStore(directory.Path);
        store.Save(5);
        File.WriteAllText(store.FilePath, contents);

        Assert.Throws<InvalidDataException>(() => store.Read());
    }

    [Fact]
    [SupportedOSPlatform("windows")]
    public void RevisionFileNeverContainsCredentialValues()
    {
        if (!OperatingSystem.IsWindows() || !WindowsAclProbe.CanSetProtectedAcl) return;
        using var directory = new TempDirectory();
        var store = NewStore(directory.Path);
        var secret = Guid.NewGuid().ToString("N");

        store.Save(42);

        var contents = File.ReadAllText(store.FilePath);
        Assert.DoesNotContain(secret, contents, StringComparison.Ordinal);
        Assert.DoesNotContain("password", contents, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("userName", contents, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    [SupportedOSPlatform("windows")]
    public void UnknownRevisionFieldIsIgnoredAndMissingRevisionIsInvalid()
    {
        if (!OperatingSystem.IsWindows() || !WindowsAclProbe.CanSetProtectedAcl) return;
        using var directory = new TempDirectory();
        var store = NewStore(directory.Path);
        store.Save(1);
        File.WriteAllText(store.FilePath, "{\"revision\":2,\"extra\":\"ignored\"}");

        Assert.Equal(2, store.Read());
        Assert.Equal(2, JsonDocument.Parse(File.ReadAllText(store.FilePath)).RootElement.GetProperty("revision").GetInt64());
    }

    [SupportedOSPlatform("windows")]
    private static SunshineAppliedCredentialStore NewStore(string dataDirectory)
    {
        var currentUser = WindowsIdentity.GetCurrent().User ?? throw new InvalidOperationException("Windows user SID is unavailable.");
        return new(dataDirectory, currentUser);
    }

    private sealed class TempDirectory : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "kr-applied-credential-" + Guid.NewGuid().ToString("N"));

        public TempDirectory() => Directory.CreateDirectory(Path);

        public void Dispose()
        {
            if (Directory.Exists(Path)) Directory.Delete(Path, recursive: true);
        }
    }
}

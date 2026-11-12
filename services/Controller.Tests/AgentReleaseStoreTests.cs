using KamenRaider.Controller.Agents;
using KamenRaider.Controller.Data;
using KamenRaider.Controller.Machines;
using KamenRaider.AgentPackage;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using System.IO.Compression;
using System.Net;
using System.Reflection;
using System.Net.Http.Json;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Controller.Tests.Fixtures;

namespace Controller.Tests;

public sealed class AgentReleaseStoreTests
{
    [Fact]
    public async Task MissingFilePartMimeIsAcceptedWhenPackageBytesValidate()
    {
        using var fixture = new ReleaseFixture();
        await using var host = await fixture.StartHost();
        using var form = new MultipartFormDataContent();
        form.Add(new StringContent("2.3.4"), "version");
        form.Add(new StringContent("stable"), "channel");
        form.Add(new StringContent("release notes"), "releaseNotes");
        form.Add(new ByteArrayContent(fixture.CreatePackage("2.3.4")), "package", "AgentPackage.zip");
        using var response = await host.Client.PostAsync("/internal/agent-releases", form);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task QuarantineRejectsReleaseUploadBeforeStagingOrRetainingArtifact()
    {
        using var fixture = new ReleaseFixture();
        fixture.Db.ActivateRestoreQuarantine("test-restore", 1);
        await using var host = await fixture.StartHost();

        using var response = await Publish(host.Client, fixture.CreatePackage("2.3.4"), "2.3.4");

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Empty(fixture.ListReleaseVersions());
        Assert.Empty(Directory.EnumerateFiles(fixture.ArtifactRoot, "*", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task QuarantineRaceAfterArtifactRetentionRemovesNewUnreferencedBytes()
    {
        using var fixture = new ReleaseFixture();
        var store = new AgentReleaseStore(fixture.ArtifactRoot);
        using var staged = await store.StageAsync(new MemoryStream(fixture.CreatePackage("2.3.4")));
        Assert.Throws<AgentReleaseConflictException>(() => store.PublishStaged(fixture.Db, staged, "2.3.4", "stable", "release notes", "admin-user",
            afterArtifactRetained: () => fixture.Db.ActivateRestoreQuarantine("test-restore-race", 1)));
        Assert.Empty(fixture.ListReleaseVersions());
        Assert.Empty(Directory.EnumerateFiles(fixture.ArtifactRoot, "*", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task RejectsDirtyVersionTokensBeforePackageValidationBecauseUpdaterCannotStartThem()
    {
        using var fixture = new ReleaseFixture();
        await using var host = await fixture.StartHost();
        const string version = "1.0.0+g0123456789ab.dirty.20261109120000";
        using var response = await Publish(host.Client, fixture.CreatePackage(version), version);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("Invalid Agent release version", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task PublishedBytesPassSharedVerifierBeforeAnyInstallation()
    {
        using var fixture = new ReleaseFixture();
        await using var host = await fixture.StartHost();
        using var response = await Publish(host.Client, fixture.CreatePackage("2.3.4"), "2.3.4");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var stored = fixture.ReadStoredRelease("2.3.4");
        using var package = File.OpenRead(stored.Path);
        var inventory = PackageVerifier.ValidateArchive(package, "2.3.4");
        Assert.Equal("win-x64", inventory.TargetRuntime);
        Assert.Equal(2, inventory.AgentUpdateChallengeProtocol);
        Assert.Equal(stored.Hash, inventory.ArchiveHash, ignoreCase: true);
        Assert.False(Path.GetFullPath(stored.Path).StartsWith(Path.Combine(fixture.Root, "public") + Path.DirectorySeparatorChar,
            StringComparison.OrdinalIgnoreCase));
    }

    [Theory]
    [InlineData("powershell.exe")]
    [InlineData("pwsh.exe")]
    public async Task CurrentUpdaterRejectsProtocolTwoWhileDefaultReleaseGateStaysClosed(string engine)
    {
        using var fixture = new ReleaseFixture();
        await using var host = await fixture.StartHost();
        using var response = await Publish(host.Client, AgentPackageCorpus.Valid(), "2.3.4");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var stored = fixture.ReadStoredRelease("2.3.4");
        var project = typeof(AgentReleaseStoreTests).Assembly.GetCustomAttributes<System.Reflection.AssemblyMetadataAttribute>()
            .Single(attribute => attribute.Key == "ControllerProjectPath").Value!;
        var repo = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(project)!, "..", ".."));
        var script = Path.Combine(fixture.Root, "validate-protocol-two.ps1");
        File.WriteAllText(script, """
            param($Updater,$Transaction,$Archive,$Hash,$Expanded)
            $ErrorActionPreference='Stop'
            . $Updater -ControllerUrl 'https://controller.example/' -MachineId ('0'*32) -OperationId ('1'*32) -UpdateTicket ('A'*43) -ReleaseVersion '2.3.4' -TargetVersion '2.3.4' -PackageSha256 $Hash -TrustedRootThumbprint ('B'*40) -AdminReturnUrl 'https://admin.example/admin/agent-releases' -DefineFunctionsOnly
            . $Transaction -DefineFunctionsOnly
            Test-AgentUpdatePackageArchive -ArchivePath $Archive -ExpectedSha256 $Hash -TargetVersion '2.3.4' | Out-Null
            Expand-AgentUpdatePackageArchive -ArchivePath $Archive -Destination $Expanded | Out-Null
            Test-AgentUpdatePackageManifest -PackageRoot $Expanded -TargetVersion '2.3.4' | Out-Null
            'Protocol-2 package passed current updater validation.'
            """);
        var start = new System.Diagnostics.ProcessStartInfo(engine) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var argument in new[] { "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", script,
            "-Updater", Path.Combine(repo, "public", "agent", "Update-Agent.ps1"),
            "-Transaction", Path.Combine(repo, "scripts", "agent", "Update-AgentPackage.ps1"),
            "-Archive", stored.Path, "-Hash", stored.Hash, "-Expanded", Path.Combine(fixture.Root, "expanded") })
            start.ArgumentList.Add(argument);
        using var process = System.Diagnostics.Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30));
        Assert.NotEqual(0, process.ExitCode);
        Assert.Contains("does not identify the requested clean release", await output + await error, StringComparison.OrdinalIgnoreCase);
        Assert.Throws<InvalidOperationException>(() => fixture.Db.ReserveAgentUpdate("machine", "2.3.4", "admin", "closed-gate"));
    }

    [Fact]
    public async Task RequiresChallengeProtocolInBothManifestAndAgentAssembly()
    {
        using var fixture = new ReleaseFixture();
        await using var host = await fixture.StartHost();
        using var legacy = await Publish(host.Client, fixture.CreatePackage("2.3.4", challengeProtocol: false), "2.3.4");
        Assert.Equal(HttpStatusCode.BadRequest, legacy.StatusCode);
        using var forged = await Publish(host.Client, fixture.CreatePackage("2.3.4", challengeProtocol: true, agentAssemblyPath: typeof(LegacyAgentMetadataFixture).Assembly.Location), "2.3.4");
        Assert.Equal(HttpStatusCode.BadRequest, forged.StatusCode);
        using var capable = await Publish(host.Client, fixture.CreatePackage("2.3.4", challengeProtocol: true), "2.3.4");
        Assert.Equal(HttpStatusCode.OK, capable.StatusCode);
    }

    [Theory]
    [InlineData("win-x64")]
    [InlineData("win-arm64")]
    public async Task PublishedReleaseStoresVerifiedWrapperRuntimeAndProtocol(string runtime)
    {
        using var fixture = new ReleaseFixture();
        await using var host = await fixture.StartHost();
        using var response = await Publish(host.Client, AgentPackageCorpus.Valid(runtime), "2.3.4");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using var connection = fixture.OpenDatabase();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT target_runtime,agent_update_challenge_protocol FROM agent_releases WHERE version='2.3.4'";
        using var row = command.ExecuteReader();
        Assert.True(row.Read());
        Assert.Equal(runtime, row.GetString(0));
        Assert.Equal(2, row.GetInt32(1));
    }
    [Theory]
    [InlineData("Update-Agent.ps1", "0123456789ab", false)]
    [InlineData("Update-AgentPackage.ps1", "0123456789ab", false)]
    [InlineData("", "0123456789abcdef", false)]
    [InlineData("", "0123456789ab", true)]
    public async Task RejectsPackagesTheUpdaterCannotConsume(string omitted, string commit, bool unexpectedRoot)
    {
        using var fixture = new ReleaseFixture();
        await using var host = await fixture.StartHost();
        using var response = await Publish(host.Client, fixture.CreatePackage("2.3.4", omitted: omitted, sourceCommit: commit, unexpectedRoot: unexpectedRoot), "2.3.4");
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Empty(fixture.ListReleaseVersions());
    }
    [Fact]
    public async Task ReleaseListAndPublishRequireAnAdminActor()
    {
        using var fixture = new ReleaseFixture();
        await using var host = await fixture.StartHost();
        host.Client.DefaultRequestHeaders.Clear();

        using var anonymousList = await host.Client.GetAsync("/internal/agent-releases");
        using var anonymousPublish = await Publish(host.Client, fixture.CreatePackage("2.3.4"), "2.3.4");
        host.Client.DefaultRequestHeaders.Add("X-Authenticated-Actor-Id", "ordinary-user");
        host.Client.DefaultRequestHeaders.Add("X-Authenticated-Actor-Role", "user");
        using var nonAdminList = await host.Client.GetAsync("/internal/agent-releases");
        host.Client.DefaultRequestHeaders.Clear();
        host.Client.DefaultRequestHeaders.Add("X-Authenticated-Actor-Id", "admin-user");
        host.Client.DefaultRequestHeaders.Add("X-Authenticated-Actor-Role", "admin");
        using var adminList = await host.Client.GetAsync("/internal/agent-releases");

        Assert.Equal(HttpStatusCode.Forbidden, anonymousList.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, anonymousPublish.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, nonAdminList.StatusCode);
        Assert.Equal(HttpStatusCode.OK, adminList.StatusCode);
        Assert.Empty(fixture.ListReleaseVersions());
    }

    [Fact]
    public async Task PublishesOnlyAValidatedPackageAndKeepsBytesPrivateAndContentAddressed()
    {
        using var fixture = new ReleaseFixture();
        await using var host = await fixture.StartHost();
        var package = fixture.CreatePackage("2.3.4");

        using var response = await Publish(host.Client, package, "2.3.4");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var dto = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("2.3.4", dto.GetProperty("version").GetString());
        Assert.Equal(Convert.ToHexString(SHA256.HashData(package)), dto.GetProperty("packageSha256").GetString());
        Assert.False(dto.TryGetProperty("packagePath", out _));
        var stored = fixture.ReadStoredRelease("2.3.4");
        Assert.Equal(Convert.ToHexString(SHA256.HashData(package)), stored.Hash);
        Assert.Equal(package.LongLength, stored.Size);
        Assert.True(File.Exists(stored.Path));
        Assert.StartsWith(fixture.ArtifactRoot, Path.GetFullPath(stored.Path), StringComparison.OrdinalIgnoreCase);
        Assert.False(Path.GetFullPath(stored.Path).StartsWith(Path.Combine(fixture.Root, "public") + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task RejectsVersionMismatchDirtyAndNonReleaseManifestsBeforePublishing()
    {
        using var fixture = new ReleaseFixture();
        await using var host = await fixture.StartHost();

        using var mismatch = await Publish(host.Client, fixture.CreatePackage("2.3.5"), "2.3.4");
        using var binaryMismatch = await Publish(host.Client, fixture.CreatePackage("2.3.4", agentLibraryVersion: "2.3.5"), "2.3.4");
        using var dirty = await Publish(host.Client, fixture.CreatePackage("2.3.4", sourceDirty: true), "2.3.4");
        using var development = await Publish(host.Client, fixture.CreatePackage("2.3.4", releaseBuild: false), "2.3.4");

        Assert.Equal(HttpStatusCode.BadRequest, mismatch.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, binaryMismatch.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, dirty.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, development.StatusCode);
        Assert.Empty(fixture.ListReleaseVersions());
    }

    [Fact]
    public async Task RejectsEmbeddedAssemblyVersionMismatchWhenManifestAndDependenciesMatch()
    {
        using var fixture = new ReleaseFixture();
        await using var host = await fixture.StartHost();
        const string releaseVersion = "2.3.4";
        var agentAssembly = typeof(KamenRaider.Agent.AgentOptions).Assembly;
        var embeddedVersion = agentAssembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()!.InformationalVersion;
        Assert.NotEqual(releaseVersion, embeddedVersion);
        Assert.Equal(releaseVersion, typeof(AgentMetadataFixture).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()!.InformationalVersion);

        var package = fixture.CreatePackage(releaseVersion, agentAssemblyPath: agentAssembly.Location);
        using var response = await Publish(host.Client, package, releaseVersion);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Empty(fixture.ListReleaseVersions());
    }

    [Fact]
    public async Task RejectsZipTraversalAndDuplicateEntryNames()
    {
        using var fixture = new ReleaseFixture();
        await using var host = await fixture.StartHost();

        using var traversal = await Publish(host.Client, fixture.CreatePackage("2.3.4", traversal: true), "2.3.4");
        using var duplicate = await Publish(host.Client, fixture.CreatePackage("2.3.4", duplicate: true), "2.3.4");
        using var duplicateManifest = await Publish(host.Client, fixture.CreatePackage("2.3.4", duplicateManifest: true), "2.3.4");

        Assert.Equal(HttpStatusCode.BadRequest, traversal.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, duplicate.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, duplicateManifest.StatusCode);
        Assert.Empty(fixture.ListReleaseVersions());
    }

    [Fact]
    public async Task RejectsPackagesOverCompressedExpandedAndEntryCountLimits()
    {
        using var fixture = new ReleaseFixture();
        await using var host = await fixture.StartHost();

        using var oversizedStream = fixture.OpenOversizedCompressedPackage();
        using var compressed = await Publish(host.Client, new StreamContent(oversizedStream), "2.3.4");
        using var expanded = await Publish(host.Client, fixture.CreateExpandedSizeSpoofedPackage("2.3.4"), "2.3.4");
        using var entries = await Publish(host.Client, fixture.CreatePackage("2.3.4", extraEntries: 5001), "2.3.4");

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, compressed.StatusCode);
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, expanded.StatusCode);
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, entries.StatusCode);
        Assert.Empty(fixture.ListReleaseVersions());
    }

    [Fact]
    public async Task ReusedVersionCannotReplaceItsPublishedHashOrBytes()
    {
        using var fixture = new ReleaseFixture();
        await using var host = await fixture.StartHost();
        var originalPackage = fixture.CreatePackage("2.3.4", marker: "first");
        var replacementPackage = fixture.CreatePackage("2.3.4", marker: "second");

        using var first = await Publish(host.Client, originalPackage, "2.3.4");
        var stored = fixture.ReadStoredRelease("2.3.4");
        var originalHash = stored.Hash;
        using var replacement = await Publish(host.Client, replacementPackage, "2.3.4");

        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, replacement.StatusCode);
        Assert.Equal(originalHash, fixture.ReadStoredRelease("2.3.4").Hash);
        Assert.Equal(originalHash, Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(stored.Path))));
    }

    [Fact]
    public async Task ProvidesVerifiedReadHandleByReleaseHashWithoutExposingThePrivatePath()
    {
        using var fixture = new ReleaseFixture();
        var store = new AgentReleaseStore(fixture.ArtifactRoot);
        var package = fixture.CreatePackage("2.3.4");
        using var staged = await store.StageAsync(new MemoryStream(package));
        var published = store.PublishStaged(fixture.Db, staged, "2.3.4", "stable", "release notes", "admin-user");

        var openMethod = typeof(AgentReleaseStore).GetMethod("OpenReadByHash");
        Assert.NotNull(openMethod);
        var handle = Assert.IsAssignableFrom<IDisposable>(openMethod.Invoke(store, [published.PackageSha256]));
        try
        {
            var handleType = handle.GetType();
            var content = Assert.IsAssignableFrom<Stream>(handleType.GetProperty("Content")?.GetValue(handle));
            var sha256 = Assert.IsType<string>(handleType.GetProperty("Sha256")?.GetValue(handle));
            var length = Assert.IsType<long>(handleType.GetProperty("Length")?.GetValue(handle));
            using var bytes = new MemoryStream();
            await content.CopyToAsync(bytes);
            Assert.Equal(published.PackageSha256, sha256);
            Assert.Equal(package.LongLength, length);
            Assert.Equal(package, bytes.ToArray());
            Assert.DoesNotContain("Path", handleType.GetProperties().Select(property => property.Name));
        }
        finally
        {
            handle.Dispose();
        }
    }

    private static Task<HttpResponseMessage> Publish(HttpClient client, byte[] package, string version)
    {
        var content = new ByteArrayContent(package);
        return Publish(client, content, version);
    }

    private static async Task<HttpResponseMessage> Publish(HttpClient client, HttpContent packageContent, string version)
    {
        using var form = new MultipartFormDataContent();
        form.Add(new StringContent(version), "version");
        form.Add(new StringContent("stable"), "channel");
        form.Add(new StringContent("release notes"), "releaseNotes");
        packageContent.Headers.ContentType = new MediaTypeHeaderValue("application/zip");
        form.Add(packageContent, "package", "AgentPackage.zip");
        return await client.PostAsync("/internal/agent-releases", form);
    }

    private sealed class ReleaseFixture : IDisposable
    {
        public ReleaseFixture()
        {
            Root = Path.Combine(Path.GetTempPath(), "kr-agent-release-store-test-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Root);
            DatabasePath = Path.Combine(Root, "controller.db");
            ArtifactRoot = Path.Combine(Root, "private", "agent-releases");
            Db = new ControllerDatabase(DatabasePath);
        }

        public string Root { get; }
        public string DatabasePath { get; }
        public string ArtifactRoot { get; }
        public ControllerDatabase Db { get; }

        public async Task<TestHost> StartHost()
        {
            var builder = WebApplication.CreateBuilder();
            builder.WebHost.UseTestServer();
            builder.Services.AddSingleton(Db);
            builder.Services.AddSingleton(new AgentReleaseStore(ArtifactRoot));
            var app = builder.Build();
            app.MapMachines();
            app.MapAgentReleases();
            await app.StartAsync();
            var client = app.GetTestClient();
            client.DefaultRequestHeaders.Add("X-Authenticated-Actor-Id", "admin-user");
            client.DefaultRequestHeaders.Add("X-Authenticated-Actor-Role", "admin");
            return new TestHost(app, client);
        }

        public byte[] CreatePackage(string version, bool sourceDirty = false, bool releaseBuild = true,
            bool traversal = false, bool duplicate = false, bool duplicateManifest = false, int extraEntries = 0,
            string marker = "package", string? agentLibraryVersion = null, string? agentAssemblyPath = null,
            string omitted = "", string sourceCommit = "0123456789ab", bool unexpectedRoot = false, bool challengeProtocol = true)
        {
            agentAssemblyPath ??= typeof(AgentMetadataFixture).Assembly.Location;
            var entries = AgentPackageCorpus.Entries();
            entries[entries.FindIndex(entry => entry.Name == "Agent/Agent.dll")] =
                ("Agent/Agent.dll", File.ReadAllBytes(agentAssemblyPath));
            var dependencyVersion = agentLibraryVersion ?? version;
            entries[entries.FindIndex(entry => entry.Name == "Agent/Agent.deps.json")] =
                ("Agent/Agent.deps.json", AgentPackageCorpus.Utf8(JsonSerializer.Serialize(
                    new { libraries = new Dictionary<string, object> { ["Agent/" + dependencyVersion] = new { type = "project" } } })));
            entries[entries.FindIndex(entry => entry.Name == "Update-Agent.ps1")] = ("Update-Agent.ps1", AgentPackageCorpus.Utf8(marker));
            if (omitted.Length > 0) entries.RemoveAll(entry => entry.Name == omitted);
            if (unexpectedRoot) entries.Add(("unexpected.txt", AgentPackageCorpus.Utf8("unexpected")));
            if (traversal) entries.Add(("../escape.txt", AgentPackageCorpus.Utf8("unsafe")));
            if (duplicate) entries.Add(("Agent/Agent.exe", AgentPackageCorpus.Utf8("replacement")));
            for (var index = 0; index < extraEntries; index++) entries.Add(($"Agent/extra/{index:D5}.txt", []));
            var manifest = new Dictionary<string, object>
            {
                ["packageFormatVersion"] = 1,
                ["agentInformationalVersion"] = version,
                ["sourceCommit"] = sourceCommit,
                ["sourceDirty"] = sourceDirty,
                ["releaseBuild"] = releaseBuild,
            };
            if (challengeProtocol) manifest["agentUpdateChallengeProtocol"] = 2;
            entries[entries.FindIndex(entry => entry.Name == "AgentPackage.manifest.json")] =
                ("AgentPackage.manifest.json", AgentPackageCorpus.Utf8(JsonSerializer.Serialize(manifest)));
            if (duplicateManifest) entries.Add(("AgentPackage.manifest.json", AgentPackageCorpus.Utf8(JsonSerializer.Serialize(manifest))));
            return AgentPackageCorpus.Build(entries);
        }

        public FileStream OpenOversizedCompressedPackage()
        {
            var path = Path.Combine(Root, "oversized.zip");
            using (var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                file.SetLength(100L * 1024 * 1024 + 1);
            return new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        }

        public byte[] CreateExpandedSizeSpoofedPackage(string version)
        {
            var bytes = CreatePackage(version);
            var entryName = Encoding.ASCII.GetBytes("Agent/Agent.exe");
            for (var index = 0; index < bytes.Length - 46; index++)
            {
                if (BitConverter.ToUInt32(bytes, index) != 0x02014b50) continue;
                var nameLength = BitConverter.ToUInt16(bytes, index + 28);
                if (nameLength != entryName.Length || !bytes.AsSpan(index + 46, nameLength).SequenceEqual(entryName)) continue;
                BitConverter.GetBytes(500L * 1024 * 1024 + 1).AsSpan(0, 4).CopyTo(bytes.AsSpan(index + 24, 4));
                return bytes;
            }
            throw new InvalidOperationException("Could not locate Agent executable ZIP entry");
        }

        public IReadOnlyList<string> ListReleaseVersions()
        {
            using var connection = OpenDatabase();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT version FROM agent_releases ORDER BY version";
            using var rows = command.ExecuteReader();
            var versions = new List<string>();
            while (rows.Read()) versions.Add(rows.GetString(0));
            return versions;
        }

        public (string Path, string Hash, long Size) ReadStoredRelease(string version)
        {
            using var connection = OpenDatabase();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT package_path,package_sha256,package_size_bytes FROM agent_releases WHERE version=$version";
            command.Parameters.AddWithValue("$version", version);
            using var row = command.ExecuteReader();
            Assert.True(row.Read());
            return (row.GetString(0), row.GetString(1), row.GetInt64(2));
        }

        public SqliteConnection OpenDatabase()
        {
            var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = DatabasePath, Pooling = false }.ToString());
            connection.Open();
            return connection;
        }

        public void Dispose()
        {
            Db.Dispose();
            var resolved = Path.GetFullPath(Root);
            var temp = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (!resolved.StartsWith(temp, StringComparison.OrdinalIgnoreCase) || !Path.GetFileName(resolved).StartsWith("kr-agent-release-store-test-", StringComparison.Ordinal))
                throw new InvalidOperationException("Unsafe release-store test cleanup path");
            if (Directory.Exists(resolved)) Directory.Delete(resolved, recursive: true);
        }
    }

    private sealed class TestHost(WebApplication app, HttpClient client) : IAsyncDisposable
    {
        public HttpClient Client { get; } = client;

        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            await app.DisposeAsync();
        }
    }
}

using System.Buffers.Binary;
using System.Diagnostics;
using System.IO.Compression;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Runtime.InteropServices;
using System.Text.Json;
using Controller.Tests.Fixtures;

namespace Controller.Tests;

public sealed class AgentPackageConformanceTests
{
    private static readonly Lazy<string> Cli = new(BuildCli);

    [Theory]
    [InlineData("win-x64")]
    [InlineData("win-arm64")]
    public void ValidPackageReportsRealAppHostRuntime(string runtime)
    {
        AssertArchive(AgentPackageCorpus.Valid(runtime), null, runtime);
    }

    [Theory]
    [InlineData("Agent/Agent.exe")]
    [InlineData("Agent/Agent.dll")]
    [InlineData("Agent/Agent.deps.json")]
    [InlineData("AgentPackage.manifest.json")]
    [InlineData("Install-AgentPackage.ps1")]
    [InlineData("Install-Service.ps1")]
    [InlineData("Install-TightVncServer.ps1")]
    [InlineData("Prepare-SunshineStreamGate.ps1")]
    [InlineData("PrivateInstallFiles.ps1")]
    [InlineData("ServiceInstallTransaction.ps1")]
    [InlineData("Update-AgentPackage.ps1")]
    [InlineData("Update-Agent.ps1")]
    public void EveryRequiredFileMustExist(string name)
    {
        var entries = AgentPackageCorpus.Entries();
        entries.RemoveAll(e => e.Name == name);
        AssertArchive(AgentPackageCorpus.Build(entries), "layout");
    }

    [Theory]
    [InlineData("Agent/../escape")]
    [InlineData("Agent/./escape")]
    [InlineData("Agent//escape")]
    [InlineData("Agent\\escape")]
    [InlineData("/Agent/escape")]
    [InlineData("Agent/file:stream")]
    [InlineData("Agent/file.")]
    [InlineData("Agent/file ")]
    [InlineData("Agent/CON.txt")]
    [InlineData("Agent/Lpt9")]
    [InlineData("Agent/bad?.txt")]
    [InlineData("Agent/é.txt")]
    public void RejectsNonportablePaths(string path) => AssertArchive(AgentPackageCorpus.Added(path), "path");

    [Theory]
    [InlineData("Agent/appsettings.json")]
    [InlineData("Agent/sub/TightVNC-release.msi")]
    [InlineData("unexpected.txt")]
    public void RejectsForbiddenLayout(string path) => AssertArchive(AgentPackageCorpus.Added(path), "layout");

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void FileParentCollisionsRejectInEitherOrder(bool parentFirst)
    {
        var entries = AgentPackageCorpus.Entries();
        var collision = new[] { ("Agent/collision", Array.Empty<byte>()), ("Agent/collision/child", Array.Empty<byte>()) };
        entries.AddRange(parentFirst ? collision : collision.Reverse());
        AssertArchive(AgentPackageCorpus.Build(entries), "path");
    }

    [Fact]
    public void CaseInsensitiveDuplicateEntriesReject() => AssertArchive(AgentPackageCorpus.Added("agent/AGENT.exe"), "path");

    [Theory]
    [InlineData(4987, false, null)]
    [InlineData(4986, true, null)]
    [InlineData(4988, false, "limit")]
    [InlineData(4989, false, "limit")]
    public void CountsPhysicalAndMaterializedNodes(int added, bool shared, string? category)
        => AssertArchive(AgentPackageCorpus.ManyNodes(added, shared), category, category is null ? "win-x64" : null);

    [Fact]
    public void ExplicitAndImplicitAncestorsShareOneDirectory()
    {
        var entries = AgentPackageCorpus.Entries();
        entries.Add(("Agent/shared/file", []));
        entries.Add(("Agent/", []));
        entries.Add(("Agent/shared/", []));
        AssertArchive(AgentPackageCorpus.Build(entries), null, "win-x64");
    }

    [Theory]
    [InlineData(100, 32, 16, null)]
    [InlineData(101, 32, 16, "path")]
    [InlineData(100, 33, 16, "path")]
    [InlineData(10, 10, 17, "path")]
    public void PathLengthSegmentAndDepthBoundsAreEnforced(int segment, int tail, int depth, string? category)
    {
        var name = depth == 16
            ? "Agent/" + new string('a', segment) + "/" + new string('b', 100) + "/" + new string('c', tail)
            : "Agent/" + string.Join("/", Enumerable.Repeat("a", depth - 2)) + "/f";
        AssertArchive(AgentPackageCorpus.Added(name), category, category is null ? "win-x64" : null);
        if (category is null)
        {
            var maxDepth = "Agent/" + string.Join("/", Enumerable.Repeat("a", 14)) + "/f";
            AssertArchive(AgentPackageCorpus.Added(maxDepth), null, "win-x64");
        }
    }

    [Fact]
    public void ValidSignedDataDescriptorsAreAccepted()
        => AssertArchive(AgentPackageCorpus.Descriptor(false), null, "win-x64");

    [Fact]
    public void ContradictoryDataDescriptorsAreRejected()
        => AssertArchive(AgentPackageCorpus.Descriptor(true), "structure");

    [Fact]
    public void UnknownZipExtraFieldsAreRejected()
        => AssertArchive(AgentPackageCorpus.CentralExtra(0x1234, []), "structure");

    [Fact]
    public void RecognizedTimestampExtraFieldIsAccepted()
        => AssertArchive(AgentPackageCorpus.CentralExtra(0x5455, [1, 0, 0, 0, 0]), null, "win-x64");

    [Fact]
    public void TimestampFormatsCannotContradictAcrossLocalAndCentralHeaders()
        => AssertArchive(AgentPackageCorpus.ContradictoryCrossTimestamp(), "structure");

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void UnicodePathExtraMustAgreeWithTheAsciiNameAndCrc(bool corrupt)
    {
        byte[] extra = [1, 0x82, 0x6b, 0xfb, 0x4f, .. AgentPackageCorpus.Utf8("Agent/Agent.exe")];
        if (corrupt) extra[1] ^= 1;
        AssertArchive(AgentPackageCorpus.CentralExtra(0x7075, extra), corrupt ? "structure" : null, corrupt ? null : "win-x64");
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void DeflateMustEndExactlyAtTheDeclaredCompressedBoundary(bool append)
        => AssertArchive(AgentPackageCorpus.AlterDeflate(append), "structure");

    [Fact]
    public void HistoricalProtocolOneAssemblyIsNotPublishable()
    {
        var entries = AgentPackageCorpus.Entries();
        var i = entries.FindIndex(e => e.Name == "Agent/Agent.dll");
        entries[i] = ("Agent/Agent.dll", File.ReadAllBytes(typeof(AgentMetadataFixture).Assembly.Location));
        AssertArchive(AgentPackageCorpus.Build(entries), "metadata");
    }

    [Fact]
    public void DuplicateCapabilityMetadataIsRejected()
    {
        var entries = AgentPackageCorpus.Entries();
        var index = entries.FindIndex(e => e.Name == "Agent/Agent.dll");
        var assembly = AgentPackageCorpus.DuplicateProtocolAssembly();
        using var pe = new PEReader(new MemoryStream(assembly));
        var metadata = pe.GetMetadataReader();
        var count = 0;
        foreach (var handle in metadata.GetAssemblyDefinition().GetCustomAttributes())
        {
            var constructor = metadata.GetCustomAttribute(handle).Constructor;
            if (constructor.Kind != HandleKind.MemberReference) continue;
            var parent = metadata.GetMemberReference((MemberReferenceHandle)constructor).Parent;
            if (parent.Kind == HandleKind.TypeReference
                && metadata.GetString(metadata.GetTypeReference((TypeReferenceHandle)parent).Name) == "AssemblyMetadataAttribute") count++;
        }
        Assert.Equal(2, count);
        entries[index] = ("Agent/Agent.dll", assembly);
        AssertArchive(AgentPackageCorpus.Build(entries), "metadata");
    }

    [Fact]
    public void UnknownExecutableMachineIsRejected()
    {
        var entries = AgentPackageCorpus.Entries();
        var i = entries.FindIndex(e => e.Name == "Agent/Agent.exe");
        var exe = (byte[])entries[i].Bytes.Clone();
        var pe = BinaryPrimitives.ReadInt32LittleEndian(exe.AsSpan(0x3c));
        BinaryPrimitives.WriteUInt16LittleEndian(exe.AsSpan(pe + 4), 0x014c);
        entries[i] = ("Agent/Agent.exe", exe);
        AssertArchive(AgentPackageCorpus.Build(entries), "runtime");
    }

    [Fact]
    public void CompressedDirectoryWithNoDeflateStreamIsCorrupt()
    {
        var entries = AgentPackageCorpus.Entries();
        entries.Add(("Agent/empty/", []));
        var bytes = AgentPackageCorpus.Mutate((b, local, central) =>
        {
            BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(local + 8), 8);
            BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(central + 10), 8);
        }, "Agent/empty/", AgentPackageCorpus.Build(entries));
        AssertArchive(bytes, "structure");
    }

    [Fact]
    public void ModifiedAssemblyVersionIsRejected()
    {
        var entries = AgentPackageCorpus.Entries();
        var index = entries.FindIndex(e => e.Name == "Agent/Agent.dll");
        var dll = (byte[])entries[index].Bytes.Clone();
        var oldVersion = AgentPackageCorpus.Utf8("2.3.4");
        var replacement = AgentPackageCorpus.Utf8("9.9.9");
        var count = 0;
        for (var i = 0; i <= dll.Length - oldVersion.Length; i++)
            if (dll.AsSpan(i, oldVersion.Length).SequenceEqual(oldVersion))
            {
                replacement.CopyTo(dll, i);
                count++;
            }
        Assert.True(count > 0);
        entries[index] = ("Agent/Agent.dll", dll);
        AssertArchive(AgentPackageCorpus.Build(entries), "metadata");
    }

    [Theory]
    [InlineData("""{"packageFormatVersion":1,"agentInformationalVersion":"2.3.4","sourceCommit":"0123456789ab","sourceDirty":false,"releaseBuild":true,"agentUpdateChallengeProtocol":1}""")]
    [InlineData("""{"packageFormatVersion":"1","agentInformationalVersion":"2.3.4","sourceCommit":"0123456789ab","sourceDirty":false,"releaseBuild":true,"agentUpdateChallengeProtocol":2}""")]
    [InlineData("""{"packageFormatVersion":1,"PackageFormatVersion":1,"agentInformationalVersion":"2.3.4","sourceCommit":"0123456789ab","sourceDirty":false,"releaseBuild":true,"agentUpdateChallengeProtocol":2}""")]
    [InlineData("""{"packageFormatVersion":1,"agentInformationalVersion":"2.3.4","sourceCommit":"0123456789AB","sourceDirty":false,"releaseBuild":true,"agentUpdateChallengeProtocol":2}""")]
    [InlineData("""{"packageFormatVersion":1,"agentInformationalVersion":"2.3.4","sourceCommit":"0123456789ab","sourceDirty":false,"releaseBuild":true,"agentUpdateChallengeProtocol":2,"extra":0}""")]
    public void ManifestMustBeStrictAndProtocolTwo(string manifest)
        => AssertArchive(AgentPackageCorpus.Replace("AgentPackage.manifest.json", manifest), "manifest");

    [Theory]
    [InlineData("""{"libraries":{"Agent/2.3.4":{"type":"project"},"agent/1.0.0":{"type":"package"}}}""")]
    [InlineData("""{"libraries":{"agent/1.0.0":{"type":"package"},"Agent/2.3.4":{"type":"project"}}}""")]
    [InlineData("""{"libraries":{"Agent/2.3.4":{"type":"project"},"agent/2.3.4":{"type":"project"}}}""")]
    [InlineData("""{"libraries":{"Agent/2.3.4":{"type":"project","Type":"project"}}}""")]
    [InlineData("""{"libraries":{"Agent/2.3.4":{"type":false}}}""")]
    [InlineData("""{"libraries":{"agent/2.3.4":{"type":"project"}}}""")]
    public void DependencyIdentityAndDuplicatesAreOrderIndependent(string deps)
        => AssertArchive(AgentPackageCorpus.Replace("Agent/Agent.deps.json", deps), "deps");

    [Fact]
    public void DependencyJsonMustBeStrictUtf8EvenInUnusedProperties()
        => AssertArchive(AgentPackageCorpus.InvalidUtf8InDeps(), "deps");

    [Theory]
    [InlineData(65536, null)]
    [InlineData(65537, "manifest")]
    public void ManifestByteLimitIsInclusive(int length, string? category)
    {
        var json = AgentPackageCorpus.Manifest.PadRight(length);
        AssertArchive(AgentPackageCorpus.Replace("AgentPackage.manifest.json", json), category, category is null ? "win-x64" : null);
    }

    [Fact]
    public void RequestedVersionHasAnInclusiveSixtyFourCharacterLimit()
    {
        var version = "1." + new string('0', 60) + ".0";
        Assert.Equal(64, version.Length);
        AssertArchive(AgentPackageCorpus.Valid(), "manifest", version: version);
        AssertArchive(AgentPackageCorpus.Valid(), "version", version: "1." + new string('0', 61) + ".0");
    }

    [Theory]
    [InlineData(16777216, null)]
    [InlineData(16777217, "deps")]
    public void DependencyByteLimitIsInclusive(int length, string? category)
    {
        var json = AgentPackageCorpus.Deps.PadRight(length);
        AssertArchive(AgentPackageCorpus.Replace("Agent/Agent.deps.json", json), category, category is null ? "win-x64" : null);
    }

    [Theory]
    [InlineData(524288000L, null)]
    [InlineData(524288001L, "limit")]
    public void ExpandedByteLimitIsInclusive(long length, string? category)
        => AssertArchive(AgentPackageCorpus.ExpandedTo(length), category, category is null ? "win-x64" : null);

    [Fact]
    public void CompressedByteLimitIsInclusive()
    {
        AssertArchive(AgentPackageCorpus.CompressedTo(104857600), null, "win-x64");
        AssertArchive(new byte[104857601], "limit");
        AssertArchive([], "limit");
    }

    [Fact]
    public void DataMutationWithoutHeaderChangesRejectsOnCrc()
        => AssertArchive(AgentPackageCorpus.CrcTamper(), "structure");

    [Theory]
    [InlineData("local-name")]
    [InlineData("local-crc")]
    [InlineData("local-size")]
    [InlineData("local-method")]
    [InlineData("split")]
    [InlineData("zip64")]
    [InlineData("overlap")]
    [InlineData("trailing")]
    public void RejectsInconsistentZipStructure(string mutation)
    {
        var archive = AgentPackageCorpus.Mutate((b, local, central) =>
        {
            switch (mutation)
            {
                case "local-name": b[local + 30] ^= 1; break;
                case "local-crc": b[local + 14] ^= 1; break;
                case "local-size": b[local + 22] ^= 1; break;
                case "local-method": b[local + 8] ^= 1; break;
                case "split": b[^18] = 1; break;
                case "zip64": BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(central + 20), uint.MaxValue); break;
                case "overlap": BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(central + 42), 0); break;
            }
        });
        if (mutation == "trailing") archive = [.. archive, 1];
        AssertArchive(archive, "structure");
    }

    [Fact]
    public void ExtractedTreeMustMatchDirectoriesAndFileBytes()
    {
        var archive = AgentPackageCorpus.Valid();
        var inventory = Invoke("ValidateArchive", new MemoryStream(archive), AgentPackageCorpus.Version)!;
        var root = Path.Combine(Path.GetTempPath(), "kr-package-tree-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            Invoke("Extract", new MemoryStream(archive), root, inventory);
            Invoke("ValidateExtracted", root, inventory);
            Directory.CreateDirectory(Path.Combine(root, "Agent", "extra"));
            AssertCategory("inventory", () => Invoke("ValidateExtracted", root, inventory));
            Directory.Delete(Path.Combine(root, "Agent", "extra"));
            File.AppendAllText(Path.Combine(root, "Agent", "Agent.deps.json"), "x");
            AssertCategory("inventory", () => Invoke("ValidateExtracted", root, inventory));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void ExtractedTreeRejectsMissingDirectoryAndHardlinkedFile()
    {
        var entries = AgentPackageCorpus.Entries();
        entries.Add(("Agent/empty/", []));
        var archive = AgentPackageCorpus.Build(entries);
        var inventory = Invoke("ValidateArchive", new MemoryStream(archive), AgentPackageCorpus.Version)!;
        var root = Path.Combine(Path.GetTempPath(), "kr-package-tree-" + Guid.NewGuid().ToString("N"));
        var outsideLink = root + "-hardlink";
        Directory.CreateDirectory(root);
        try
        {
            Invoke("Extract", new MemoryStream(archive), root, inventory);
            Directory.Delete(Path.Combine(root, "Agent", "empty"));
            AssertCategory("inventory", () => Invoke("ValidateExtracted", root, inventory));
            Directory.CreateDirectory(Path.Combine(root, "Agent", "empty"));
            Assert.True(CreateHardLinkW(outsideLink, Path.Combine(root, "Update-Agent.ps1"), IntPtr.Zero));
            AssertCategory("inventory", () => Invoke("ValidateExtracted", root, inventory));
        }
        finally
        {
            if (File.Exists(outsideLink)) File.Delete(outsideLink);
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public void ExtractedTreeRejectsJunctionReplacingExpectedDirectory()
    {
        var archive = AgentPackageCorpus.Valid();
        var inventory = Invoke("ValidateArchive", new MemoryStream(archive), AgentPackageCorpus.Version)!;
        var root = Path.Combine(Path.GetTempPath(), "kr-package-tree-" + Guid.NewGuid().ToString("N"));
        var target = root + "-target";
        var junction = Path.Combine(root, "Agent");
        Directory.CreateDirectory(root);
        try
        {
            Invoke("Extract", new MemoryStream(archive), root, inventory);
            Directory.Move(junction, target);
            var (code, output) = Run("cmd.exe", ["/d", "/c", "mklink", "/J", junction, target]);
            Assert.True(code == 0, output);
            AssertCategory("inventory", () => Invoke("ValidateExtracted", root, inventory));
        }
        finally
        {
            if (Directory.Exists(junction)) Directory.Delete(junction);
            if (Directory.Exists(target)) Directory.Delete(target, true);
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public void ExtractionRequiresEmptyRootAndChecksTheWindowsPathBound()
    {
        var archive = AgentPackageCorpus.Valid();
        var inventory = Invoke("ValidateArchive", new MemoryStream(archive), AgentPackageCorpus.Version)!;
        var basePath = Path.Combine(Path.GetTempPath(), "kr-package-long-" + Guid.NewGuid().ToString("N"));
        var maxRelative = Math.Max(AgentPackageCorpus.Scripts.Max(s => s.Length), "Agent/Agent.deps.json".Length);
        var maximumRoot = 247 - 1 - maxRelative;
        var root = basePath;
        while (root.Length < maximumRoot)
        {
            var available = maximumRoot - root.Length - 1;
            if (available < 1) throw new InvalidOperationException("Cannot construct boundary path.");
            root = Path.Combine(root, new string('a', Math.Min(80, available)));
        }
        Directory.CreateDirectory(root);
        try
        {
            var tooLong = Path.Combine(root, "x");
            Directory.CreateDirectory(tooLong);
            AssertCategory("inventory", () => Invoke("Extract", new MemoryStream(archive), tooLong, inventory));
            Directory.Delete(tooLong);
            File.WriteAllText(Path.Combine(root, "preexisting"), "keep");
            AssertCategory("inventory", () => Invoke("Extract", new MemoryStream(archive), root, inventory));
            File.Delete(Path.Combine(root, "preexisting"));
            Invoke("Extract", new MemoryStream(archive), root, inventory);
            Invoke("ValidateExtracted", root, inventory);
        }
        finally { Directory.Delete(basePath, true); }
    }

    [Fact]
    public void ValidationAndExtractionNeverExecutePackageScripts()
    {
        var root = Path.Combine(Path.GetTempPath(), "kr-package-sentinel-" + Guid.NewGuid().ToString("N"));
        var sentinel = root + "-executed";
        var entries = AgentPackageCorpus.Entries();
        var index = entries.FindIndex(e => e.Name == "Update-Agent.ps1");
        entries[index] = ("Update-Agent.ps1", AgentPackageCorpus.Utf8("Set-Content -LiteralPath '" + sentinel + "' -Value executed"));
        var archive = AgentPackageCorpus.Build(entries);
        Directory.CreateDirectory(root);
        try
        {
            AssertArchive(archive, null, "win-x64");
            var inventory = Invoke("ValidateArchive", new MemoryStream(archive), AgentPackageCorpus.Version)!;
            Invoke("Extract", new MemoryStream(archive), root, inventory);
            Assert.False(File.Exists(sentinel));
        }
        finally
        {
            if (File.Exists(sentinel)) File.Delete(sentinel);
            Directory.Delete(root, true);
        }
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateHardLinkW(string newName, string existingName, IntPtr security);

    private static object? Invoke(string method, params object[] args)
    {
        var type = Type.GetType("KamenRaider.AgentPackage.PackageVerifier, AgentPackage");
        Assert.NotNull(type);
        try { return type.GetMethod(method, BindingFlags.Public | BindingFlags.Static)!.Invoke(null, args); }
        catch (TargetInvocationException e) when (e.InnerException is not null) { throw e.InnerException; }
    }

    private static void AssertCategory(string category, Action action)
    {
        var error = Record.Exception(action);
        Assert.NotNull(error);
        Assert.Equal(category, error.GetType().GetProperty("Category")?.GetValue(error));
    }

    private static void AssertArchive(byte[] bytes, string? category, string? runtime = null, string version = AgentPackageCorpus.Version)
    {
        object? inventory = null;
        if (category is null) inventory = Invoke("ValidateArchive", new MemoryStream(bytes), version);
        else AssertCategory(category, () => Invoke("ValidateArchive", new MemoryStream(bytes), version));
        if (inventory is not null) Assert.Equal(runtime, inventory.GetType().GetProperty("TargetRuntime")!.GetValue(inventory));

        var root = Path.Combine(Path.GetTempPath(), "kr-package-cli-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var archive = Path.Combine(root, "package.zip");
            var output = Path.Combine(root, "result.json");
            File.WriteAllBytes(archive, bytes);
            var (code, stdout) = Run("dotnet", [Cli.Value, "validate-archive", "--archive", archive, "--version", version, "--output-json", output]);
            Assert.True(File.Exists(output), stdout);
            using var json = JsonDocument.Parse(File.ReadAllBytes(output));
            Assert.Equal(category is null ? 0 : 1, code);
            Assert.Equal(category is null ? "accepted" : "rejected", json.RootElement.GetProperty("status").GetString());
            Assert.Equal(category, json.RootElement.GetProperty("category").GetString());
            Assert.Equal(runtime, json.RootElement.GetProperty("runtime").GetString());
        }
        finally { Directory.Delete(root, true); }
    }

    private static string BuildCli()
    {
        var project = typeof(AgentPackageConformanceTests).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
            .Single(a => a.Key == "ControllerProjectPath").Value!;
        var path = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(project)!, "..", "AgentPackageVerifier", "AgentPackageVerifier.csproj"));
        var (code, output) = Run("dotnet", ["build", path, "-c", "Release", "--nologo"]);
        Assert.True(code == 0, output);
        return Path.Combine(Path.GetDirectoryName(path)!, "bin", "Release", "net10.0", "AgentPackageVerifier.dll");
    }

    private static (int Code, string Output) Run(string file, string[] args)
    {
        var start = new ProcessStartInfo(file) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var arg in args) start.ArgumentList.Add(arg);
        using var process = Process.Start(start)!;
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        process.WaitForExit();
        return (process.ExitCode, stdout.Result + stderr.Result);
    }
}

using System.Buffers.Binary;
using System.Diagnostics;
using System.IO.Compression;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Runtime.InteropServices;
using System.Text.Json;
using Controller.Tests.Fixtures;
using KamenRaider.AgentPackage;

namespace Controller.Tests;

public sealed class AgentPackageConformanceTests
{
    private static readonly Lazy<string> Cli = new(BuildCli);
    private delegate void InspectDeflate(ReadOnlySpan<byte> data, int expectedLength);
    private static readonly Lazy<InspectDeflate> Inspector = new(() =>
        typeof(PackageVerifier).Assembly.GetType("KamenRaider.AgentPackage.DeflateInspector", throwOnError: true)!
            .GetMethod("Verify", BindingFlags.NonPublic | BindingFlags.Static)!.CreateDelegate<InspectDeflate>());

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
    [InlineData("\\Agent/file")]
    [InlineData("Agent/a//")]
    [InlineData("Agent/a/../b")]
    [InlineData("Agent/PRN.log")]
    [InlineData("Agent/aux")]
    [InlineData("Agent/NUL.bin")]
    [InlineData("Agent/COM1.dat")]
    [InlineData("Agent/com9")]
    [InlineData("Agent/LPT1.txt")]
    [InlineData("Agent/lpt9.log")]
    [InlineData("Agent/a<b")]
    [InlineData("Agent/a>b")]
    [InlineData("Agent/a\"b")]
    [InlineData("Agent/a|b")]
    [InlineData("Agent/a*b")]
    [InlineData("Agent/a\u007fb")]
    public void RejectsAllWindowsDeviceNamesAndForbiddenCharacters(string path)
        => AssertArchive(AgentPackageCorpus.Added(path), "path");

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(31)]
    [InlineData(127)]
    public void RejectsControlBytesInZipNames(byte value)
    {
        var bytes = AgentPackageCorpus.Mutate((b, local, central) =>
        {
            b[local + 30] = value;
            b[central + 46] = value;
        });
        AssertArchive(bytes, "path");
    }

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

    [Fact]
    public void ExplicitDirectoryEntriesCannotBeDuplicated()
    {
        var entries = AgentPackageCorpus.Entries();
        entries.Add(("Agent/shared/", []));
        entries.Add(("agent/SHARED/", []));
        AssertArchive(AgentPackageCorpus.Build(entries), "path");
    }

    [Theory]
    [InlineData("Agent/Agent.exe")]
    [InlineData("AgentPackage.manifest.json")]
    [InlineData("Update-Agent.ps1")]
    public void RequiredFileCannotBeReplacedByDirectory(string name)
    {
        var entries = AgentPackageCorpus.Entries();
        entries.RemoveAll(e => e.Name == name);
        entries.Add((name + "/", []));
        AssertArchive(AgentPackageCorpus.Build(entries), "layout");
    }

    [Fact]
    public void ExactlyFiveThousandPhysicalAndMaterializedNodesAreAllowed()
    {
        var entries = AgentPackageCorpus.Entries();
        for (var i = 0; i < 4987; i++) entries.Add(($"Agent/f{i:0000}", []));
        entries.Add(("Agent/", []));
        Assert.Equal(5000, entries.Count);
        AssertArchive(AgentPackageCorpus.Build(entries), null, "win-x64");
    }

    [Fact]
    public void FiveThousandEntriesWithManyDistinctImplicitParentsExceedNodeLimit()
    {
        var entries = AgentPackageCorpus.Entries();
        for (var i = 0; i < 4988; i++) entries.Add(($"Agent/d{i:0000}/file", []));
        Assert.Equal(5000, entries.Count);
        AssertArchive(AgentPackageCorpus.Build(entries), "limit");
    }

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
    public void ValidUnsignedDataDescriptorIsAccepted()
        => AssertArchive(AgentPackageCorpus.DescriptorVariant("unsigned"), null, "win-x64");

    [Theory]
    [InlineData("unsigned-crc")]
    [InlineData("signed-crc")]
    [InlineData("signed-compressed")]
    [InlineData("signed-expanded")]
    [InlineData("local-crc")]
    [InlineData("local-compressed")]
    [InlineData("local-expanded")]
    public void DataDescriptorAndLocalHeaderMustMatchCentralDirectory(string variant)
        => AssertArchive(AgentPackageCorpus.DescriptorVariant(variant), "structure");

    [Fact]
    public void UnknownZipExtraFieldsAreRejected()
        => AssertArchive(AgentPackageCorpus.CentralExtra(0x1234, []), "structure");

    [Fact]
    public void Zip64ExtraFieldIsRejectedEvenWithoutSentinelSizes()
        => AssertArchive(AgentPackageCorpus.CentralExtra(0x0001, new byte[16]), "structure");

    [Fact]
    public void DuplicateRecognizedExtraFieldsAreRejected()
        => AssertArchive(AgentPackageCorpus.CentralExtras((0x5455, [1, 0, 0, 0, 0]), (0x5455, [1, 0, 0, 0, 0])), "structure");

    [Theory]
    [InlineData(0x5455, "")]
    [InlineData(0x5455, "08")]
    [InlineData(0x5455, "0100")]
    [InlineData(0x000a, "00000000")]
    [InlineData(0x7075, "01")]
    public void MalformedRecognizedExtraFieldsAreRejected(int id, string hex)
        => AssertArchive(AgentPackageCorpus.CentralExtra((ushort)id, Convert.FromHexString(hex)), "structure");

    [Fact]
    public void ExtraFieldDeclaredLengthCannotOverrunItsContainer()
    {
        var bytes = AgentPackageCorpus.CentralExtra(0x5455, [1, 0, 0, 0, 0]);
        var end = bytes.Length - 22;
        var central = (int)BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(end + 16));
        var nameLength = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(central + 28));
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(central + 46 + nameLength + 2), 6);
        AssertArchive(bytes, "structure");
    }

    [Fact]
    public void ValidNtfsTimestampExtraIsAccepted()
    {
        var value = new byte[32];
        BinaryPrimitives.WriteUInt16LittleEndian(value.AsSpan(4), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(value.AsSpan(6), 24);
        AssertArchive(AgentPackageCorpus.CentralExtra(0x000a, value), null, "win-x64");
    }

    [Fact]
    public void RecognizedTimestampExtraFieldIsAccepted()
        => AssertArchive(AgentPackageCorpus.CentralExtra(0x5455, [1, 0, 0, 0, 0]), null, "win-x64");

    [Theory]
    [InlineData(116444736010000000L)]
    [InlineData(116444735999999999L)]
    public void TimestampFormatsCannotContradictAcrossLocalAndCentralHeaders(long ntfsTicks)
        => AssertArchive(AgentPackageCorpus.ContradictoryCrossTimestamp(ntfsTicks), "structure");

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

    [Theory]
    [InlineData("valid", null)]
    [InlineData("bad-complement", "structure")]
    [InlineData("reserved-block", "structure")]
    public void DeflateStoredBlocksRequireCorrectStructure(string variant, string? category)
        => AssertArchive(AgentPackageCorpus.DeflateStoredBlock(variant), category, category is null ? "win-x64" : null);

    // These raw DEFLATE vectors isolate the named tree and code faults.
    [Theory]
    [InlineData("oversubscribed code-length tree", "05009200")]
    [InlineData("incomplete code-length tree", "05000200")]
    [InlineData("repeat exceeds total code lengths", "050080e4ff1f")]
    [InlineData("repeat before first code length", "05000224")]
    [InlineData("missing end-of-block code", "050080e47f1b")]
    [InlineData("incomplete literal tree", "0580010500000080febf0e")]
    [InlineData("oversubscribed literal tree", "05c0010500000000a0fa7f3a")]
    [InlineData("incomplete distance tree", "05c1010900000080a0ffaf2d")]
    [InlineData("oversubscribed distance tree", "05c2010500000000a0ffafab00")]
    [InlineData("reserved fixed literal/length symbol 286", "1b03")]
    [InlineData("reserved fixed distance symbol 30", "73043e")]
    [InlineData("reserved fixed distance symbol 31", "73047e")]
    [InlineData("back-reference exceeds output history", "730442")]
    public void InvalidHuffmanTreesAndCodesAreRejected(string _, string hex)
    {
        var payload = Convert.FromHexString(hex);
        var error = Assert.Throws<PackageValidationException>(() => Inspector.Value(payload, 15));
        Assert.Equal("structure", error.Category);
        AssertArchive(AgentPackageCorpus.DeflatePayload(payload), "structure");
    }

    [Fact]
    public void ValidSingleEndCodeAndEmptyDistanceTreeAreAccepted()
    {
        var payload = Convert.FromHexString("05c0010500000000a0ffaf03");
        Inspector.Value(payload, 0);
        AssertArchive(AgentPackageCorpus.DeflatePayload(payload, emptyOriginal: true), null, "win-x64");
    }

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
    public void DuplicateInformationalVersionMetadataIsRejected()
    {
        var assembly = AgentPackageCorpus.DuplicateInformationalVersionAssembly();
        using var pe = new PEReader(new MemoryStream(assembly));
        var metadata = pe.GetMetadataReader();
        var count = 0;
        foreach (var handle in metadata.GetAssemblyDefinition().GetCustomAttributes())
        {
            var constructor = metadata.GetCustomAttribute(handle).Constructor;
            if (constructor.Kind != HandleKind.MemberReference) continue;
            var parent = metadata.GetMemberReference((MemberReferenceHandle)constructor).Parent;
            if (parent.Kind == HandleKind.TypeReference
                && metadata.GetString(metadata.GetTypeReference((TypeReferenceHandle)parent).Name) == "AssemblyInformationalVersionAttribute") count++;
        }
        Assert.Equal(2, count);
        var entries = AgentPackageCorpus.Entries();
        entries[entries.FindIndex(e => e.Name == "Agent/Agent.dll")] = ("Agent/Agent.dll", assembly);
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

    [Theory]
    [InlineData("missing")]
    [InlineData("malformed")]
    [InlineData("dll-image")]
    [InlineData("wrong-magic")]
    [InlineData("not-executable")]
    public void ExecutableMustBeAValidMatchingArchitecturePe(string mutation)
    {
        var entries = AgentPackageCorpus.Entries();
        var index = entries.FindIndex(e => e.Name == "Agent/Agent.exe");
        var exe = (byte[])entries[index].Bytes.Clone();
        var pe = BinaryPrimitives.ReadInt32LittleEndian(exe.AsSpan(0x3c));
        switch (mutation)
        {
            case "missing": entries.RemoveAt(index); AssertArchive(AgentPackageCorpus.Build(entries), "layout"); return;
            case "malformed": exe = AgentPackageCorpus.Utf8("not a PE image"); break;
            case "dll-image": exe = entries.Single(e => e.Name == "Agent/Agent.dll").Bytes; break;
            case "wrong-magic": BinaryPrimitives.WriteUInt16LittleEndian(exe.AsSpan(pe + 24), 0x010b); break;
            case "not-executable":
                var characteristics = BinaryPrimitives.ReadUInt16LittleEndian(exe.AsSpan(pe + 22));
                BinaryPrimitives.WriteUInt16LittleEndian(exe.AsSpan(pe + 22), (ushort)(characteristics & ~2));
                break;
        }
        entries[index] = ("Agent/Agent.exe", exe);
        AssertArchive(AgentPackageCorpus.Build(entries), "runtime");
    }

    [Theory]
    [InlineData("non-pe")]
    [InlineData("missing-protocol")]
    [InlineData("missing-version")]
    [InlineData("malformed-protocol")]
    public void AssemblyRequiresValidAndCompleteMetadata(string mutation)
    {
        var entries = AgentPackageCorpus.Entries();
        var index = entries.FindIndex(e => e.Name == "Agent/Agent.dll");
        var dll = (byte[])entries[index].Bytes.Clone();
        switch (mutation)
        {
            case "non-pe":
                dll = AgentPackageCorpus.Utf8("not a managed PE");
                break;
            case "missing-protocol":
                dll = File.ReadAllBytes(typeof(LegacyAgentMetadataFixture).Assembly.Location);
                break;
            case "missing-version":
                var attributeName = AgentPackageCorpus.Utf8("AssemblyInformationalVersionAttribute");
                var nameOffset = dll.AsSpan().IndexOf(attributeName);
                Assert.True(nameOffset >= 0);
                dll[nameOffset] = (byte)'X';
                break;
            case "malformed-protocol":
                var key = AgentPackageCorpus.Utf8("AgentUpdateChallengeProtocol");
                byte[] blob = [1, 0, (byte)key.Length, .. key, 1, (byte)'2', 0, 0];
                var blobOffset = dll.AsSpan().IndexOf(blob);
                Assert.True(blobOffset >= 0);
                dll[blobOffset] = 0;
                break;
        }
        entries[index] = ("Agent/Agent.dll", dll);
        AssertArchive(AgentPackageCorpus.Build(entries), "metadata");
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

    [Fact]
    public void ManifestFieldsMayAppearInAnyOrder()
    {
        const string reversed = """
            { "agentUpdateChallengeProtocol":2, "releaseBuild":true, "sourceDirty":false,
              "sourceCommit":"0123456789ab", "agentInformationalVersion":"2.3.4", "packageFormatVersion":1 }
            """;
        AssertArchive(AgentPackageCorpus.Replace("AgentPackage.manifest.json", reversed), null, "win-x64");
    }

    public static IEnumerable<object[]> ManifestFieldCases()
    {
        var fields = new (string Key, string Original, string[] Invalid)[]
        {
            ("packageFormatVersion", "1", ["0", "2", "1.0", "1e0", "\"1\"", "true", "null", "[]"]),
            ("agentInformationalVersion", "\"2.3.4\"", ["\"2.3.5\"", "\"\"", "1", "false", "null", "[]"]),
            ("sourceCommit", "\"0123456789ab\"", ["\"0123456789a\"", "\"0123456789abc\"", "\"0123456789aG\"", "\"0123456789AB\"", "12", "null", "[]"]),
            ("sourceDirty", "false", ["true", "0", "\"false\"", "null", "[]"]),
            ("releaseBuild", "true", ["false", "1", "\"true\"", "null", "[]"]),
            ("agentUpdateChallengeProtocol", "2", ["0", "1", "3", "2.0", "2e0", "\"2\"", "false", "null", "[]"])
        };
        foreach (var (key, original, values) in fields)
            foreach (var value in values)
            {
                var search = "\"" + key + "\":" + original;
                if (!AgentPackageCorpus.Manifest.Contains(search, StringComparison.Ordinal)) throw new InvalidOperationException("Fixture field missing.");
                yield return [key + "=" + value, AgentPackageCorpus.Manifest.Replace(search, "\"" + key + "\":" + value, StringComparison.Ordinal)];
            }
    }

    [Theory]
    [MemberData(nameof(ManifestFieldCases))]
    public void ManifestValuesNeverCoerceOrPermitUnsupportedValues(string _, string manifest)
        => AssertArchive(AgentPackageCorpus.Replace("AgentPackage.manifest.json", manifest), "manifest");

    [Theory]
    [InlineData("packageFormatVersion", "1")]
    [InlineData("agentInformationalVersion", "\"2.3.4\"")]
    [InlineData("sourceCommit", "\"0123456789ab\"")]
    [InlineData("sourceDirty", "false")]
    [InlineData("releaseBuild", "true")]
    [InlineData("agentUpdateChallengeProtocol", "2")]
    public void ManifestRequiresEveryFieldExactlyOnceWithExactCase(string name, string value)
    {
        var field = "\"" + name + "\":" + value;
        var missing = AgentPackageCorpus.Manifest.Replace(field + ",", "", StringComparison.Ordinal)
            .Replace("," + field, "", StringComparison.Ordinal);
        AssertArchive(AgentPackageCorpus.Replace("AgentPackage.manifest.json", missing), "manifest");
        var alias = char.ToUpperInvariant(name[0]) + name[1..];
        var wrongCase = AgentPackageCorpus.Manifest.Replace("\"" + name + "\"", "\"" + alias + "\"", StringComparison.Ordinal);
        AssertArchive(AgentPackageCorpus.Replace("AgentPackage.manifest.json", wrongCase), "manifest");
        foreach (var duplicate in new[] { name, alias })
        {
            var duplicateFirst = "{\"" + duplicate + "\":" + value + "," + AgentPackageCorpus.Manifest[1..];
            var duplicateLast = AgentPackageCorpus.Manifest[..^1] + ",\"" + duplicate + "\":" + value + "}";
            AssertArchive(AgentPackageCorpus.Replace("AgentPackage.manifest.json", duplicateFirst), "manifest");
            AssertArchive(AgentPackageCorpus.Replace("AgentPackage.manifest.json", duplicateLast), "manifest");
        }
    }

    [Theory]
    [InlineData("")]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("{}")]
    [InlineData("{\"packageFormatVersion\":1,}")]
    [InlineData("/*comment*/{}")]
    [InlineData("{\"packageFormatVersion\":1} trailing")]
    public void ManifestMustBeOneStrictJsonObject(string json)
        => AssertArchive(AgentPackageCorpus.Replace("AgentPackage.manifest.json", json), "manifest");

    [Fact]
    public void ManifestMustBeStrictUtf8()
    {
        var entries = AgentPackageCorpus.Entries();
        var index = entries.FindIndex(e => e.Name == "AgentPackage.manifest.json");
        var json = AgentPackageCorpus.Utf8(AgentPackageCorpus.Manifest);
        json[Array.IndexOf(json, (byte)'a')] = 0x80;
        entries[index] = ("AgentPackage.manifest.json", json);
        AssertArchive(AgentPackageCorpus.Build(entries), "manifest");
    }

    [Theory]
    [InlineData("AgentPackage.manifest.json", "manifest")]
    [InlineData("Agent/Agent.deps.json", "deps")]
    public void JsonDoesNotPermitAnAdditionalUtf8Preamble(string name, string category)
    {
        var entries = AgentPackageCorpus.Entries();
        var index = entries.FindIndex(e => e.Name == name);
        entries[index] = (name, [0xef, 0xbb, 0xbf, .. entries[index].Bytes]);
        AssertArchive(AgentPackageCorpus.Build(entries), category);
    }

    [Theory]
    [InlineData(7)]
    [InlineData(8)]
    public void ManifestRejectsNestedExtraFieldsAtAndBeyondItsDepthLimit(int arrayDepth)
    {
        var json = AgentPackageCorpus.Manifest[..^1] + ",\"extra\":" + new string('[', arrayDepth) + "0" + new string(']', arrayDepth) + "}";
        AssertArchive(AgentPackageCorpus.Replace("AgentPackage.manifest.json", json), "manifest");
    }

    [Theory]
    [InlineData("""{"libraries":{"Agent/2.3.4":{"type":"project"},"agent/1.0.0":{"type":"package"}}}""")]
    [InlineData("""{"libraries":{"agent/1.0.0":{"type":"package"},"Agent/2.3.4":{"type":"project"}}}""")]
    [InlineData("""{"libraries":{"Agent/2.3.4":{"type":"project"},"agent/2.3.4":{"type":"project"}}}""")]
    [InlineData("""{"libraries":{"Agent/2.3.4":{"type":"project","Type":"project"}}}""")]
    [InlineData("""{"libraries":{"Agent/2.3.4":{"type":false}}}""")]
    [InlineData("""{"libraries":{"agent/2.3.4":{"type":"project"}}}""")]
    public void DependencyIdentityAndDuplicatesAreOrderIndependent(string deps)
        => AssertArchive(AgentPackageCorpus.Replace("Agent/Agent.deps.json", deps), "deps");

    [Theory]
    [InlineData("")]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("{}")]
    [InlineData("""{"Libraries":{"Agent/2.3.4":{"type":"project"}}}""")]
    [InlineData("""{"libraries":null}""")]
    [InlineData("""{"libraries":[]}""")]
    [InlineData("""{"libraries":"object"}""")]
    [InlineData("""{"libraries":{"Agent/2.3.4":null}}""")]
    [InlineData("""{"libraries":{"Agent/2.3.4":"project"}}""")]
    [InlineData("""{"libraries":{"Agent/2.3.4":{}}}""")]
    [InlineData("""{"libraries":{"Agent/2.3.4":{"Type":"project"}}}""")]
    [InlineData("""{"libraries":{"Agent/2.3.4":{"type":"Project"}}}""")]
    [InlineData("""{"libraries":{"Agent/2.3.4":{"type":"package"}}}""")]
    [InlineData("""{"libraries":{"Agent/2.3.4":{"type":null}}}""")]
    [InlineData("""{"libraries":{"Agent/2.3.4":{"type":1}}}""")]
    [InlineData("""{"libraries":{"Agent/2.3.4":{"type":"project","type":"project"}}}""")]
    [InlineData("""{"libraries":{"Agent/2.3.5":{"type":"project"}}}""")]
    [InlineData("""{"libraries":{"Agent":{"type":"project"}}}""")]
    [InlineData("""{"libraries":{"Agent/2.3.4/extra":{"type":"project"}}}""")]
    [InlineData("""{"libraries":{"Agent/2.3.4":{"type":"project"}},"Libraries":{}}""")]
    [InlineData("""{"libraries":{"Agent/2.3.4":{"type":"project"}},"other":{"k":1,"K":2}}""")]
    [InlineData("""{"libraries":{"Agent/2.3.4":{"type":"project"}},"other":[{"k":1,"k":2}]}""")]
    [InlineData("""{"libraries":{"Agent/2.3.4":{"type":"project"}},"other":0,"other":1}""")]
    public void DependenciesRejectMalformedObjectsTypesAndDuplicates(string deps)
        => AssertArchive(AgentPackageCorpus.Replace("Agent/Agent.deps.json", deps), "deps");

    [Theory]
    [InlineData("""{"libraries":{"Other/1.0.0":{"type":"project"},"Agent/2.3.4":{"type":"project"}}}""")]
    [InlineData("""{"libraries":{"Agent/2.3.4":{"type":"project"},"Other/1.0.0":{"type":"package"}}}""")]
    public void DependenciesAllowOtherProjectsInEitherOrder(string deps)
        => AssertArchive(AgentPackageCorpus.Replace("Agent/Agent.deps.json", deps), null, "win-x64");

    [Theory]
    [InlineData(31, null)]
    [InlineData(32, "deps")]
    public void DependencyJsonDepthLimitIsInclusive(int arrayDepth, string? category)
    {
        var deps = """{"libraries":{"Agent/2.3.4":{"type":"project"}},"nested":"""
            + new string('[', arrayDepth) + "0" + new string(']', arrayDepth) + "}";
        AssertArchive(AgentPackageCorpus.Replace("Agent/Agent.deps.json", deps), category, category is null ? "win-x64" : null);
    }

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
    [InlineData("2.3", "version")]
    [InlineData("2.3.4.5", "version")]
    [InlineData("-2.3.4", "version")]
    [InlineData("2.3.4-dirty", "version")]
    [InlineData("2.3.4 ", "version")]
    [InlineData("2.3.4\n", "version")]
    [InlineData("２.3.4", "version")]
    [InlineData("1.0.0+g0123456789a", "version")]
    [InlineData("1.0.0+g0123456789AB", "version")]
    [InlineData("1.0.0+g0123456789ab", "manifest")]
    [InlineData("1.0.0+g0123456789abcdef0123456789abcdef01234567", "manifest")]
    [InlineData("1.0.0+g0123456789abcdef0123456789abcdef012345678", "version")]
    public void RequestedVersionUsesTheExactAsciiGrammar(string version, string category)
        => AssertArchive(AgentPackageCorpus.Valid(), category, version: version);

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
        AssertArchive([0], "structure");
    }

    [Fact]
    public void CrcOnlyTamperRejectsEvenThoughZipArchiveReadsTheChangedData()
    {
        var bytes = AgentPackageCorpus.CrcTamper();
        using var zip = new ZipArchive(new MemoryStream(bytes), ZipArchiveMode.Read);
        using var reader = new StreamReader(zip.GetEntry("Update-Agent.ps1")!.Open());
        Assert.NotEqual("# inert fixture", reader.ReadToEnd());
        AssertArchive(bytes, "structure");
    }

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

    [Theory]
    [InlineData("eocd-other-disk")]
    [InlineData("eocd-central-disk")]
    [InlineData("eocd-count-mismatch")]
    [InlineData("central-other-disk")]
    [InlineData("zip64-count")]
    [InlineData("zip64-central-size")]
    [InlineData("zip64-central-offset")]
    [InlineData("zip64-entry-size")]
    [InlineData("zip64-entry-offset")]
    [InlineData("encrypted")]
    [InlineData("unknown-flags")]
    [InlineData("unsupported-method")]
    [InlineData("local-flags")]
    [InlineData("local-compressed-size")]
    [InlineData("actual-crc")]
    [InlineData("actual-expanded-size")]
    [InlineData("overlap-data")]
    [InlineData("prefix")]
    [InlineData("truncated")]
    public void RejectsZip64SplitEncryptionOverflowAndCorruptSpans(string mutation)
    {
        var source = mutation == "actual-expanded-size"
            ? AgentPackageCorpus.Build(AgentPackageCorpus.Entries(), CompressionLevel.Optimal)
            : AgentPackageCorpus.Valid();
        var bytes = AgentPackageCorpus.Mutate((b, local, central) =>
        {
            var end = b.Length - 22;
            switch (mutation)
            {
                case "eocd-other-disk": b[end + 4] = 1; break;
                case "eocd-central-disk": b[end + 6] = 1; break;
                case "eocd-count-mismatch": b[end + 8] ^= 1; break;
                case "central-other-disk": b[central + 34] = 1; break;
                case "zip64-count":
                    BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(end + 8), ushort.MaxValue);
                    BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(end + 10), ushort.MaxValue); break;
                case "zip64-central-size": BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(end + 12), uint.MaxValue); break;
                case "zip64-central-offset": BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(end + 16), uint.MaxValue); break;
                case "zip64-entry-size": BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(central + 24), uint.MaxValue); break;
                case "zip64-entry-offset": BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(central + 42), uint.MaxValue); break;
                case "encrypted": b[local + 6] |= 1; b[central + 8] |= 1; break;
                case "unknown-flags": b[local + 6] |= 0x20; b[central + 8] |= 0x20; break;
                case "unsupported-method": b[local + 8] = 99; b[central + 10] = 99; break;
                case "local-flags": b[local + 6] ^= 8; break;
                case "local-compressed-size": b[local + 18] ^= 1; break;
                case "actual-crc": b[local + 14] ^= 1; b[central + 16] ^= 1; break;
                case "actual-expanded-size":
                    var actual = BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(central + 24));
                    BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(central + 24), actual + 1);
                    BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(local + 22), actual + 1); break;
                case "overlap-data":
                    var compressed = BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(central + 20));
                    BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(central + 20), compressed + 1);
                    BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(local + 18), compressed + 1); break;
            }
        }, "Update-Agent.ps1", source);
        if (mutation == "prefix") bytes = [0, .. bytes];
        if (mutation == "truncated") bytes = bytes[..^1];
        AssertArchive(bytes, "structure");
    }

    [Fact]
    public void Zip64LocatorCannotAppearBetweenCentralDirectoryAndEocd()
    {
        var bytes = AgentPackageCorpus.Valid();
        var end = bytes.Length - 22;
        byte[] locator = [0x50, 0x4b, 0x06, 0x07, .. new byte[16]];
        AssertArchive([.. bytes[..end], .. locator, .. bytes[end..]], "structure");
    }

    [Fact]
    public void ArchiveCommentIsAllowedButNotTrailingData()
    {
        var bytes = AgentPackageCorpus.Valid();
        var end = bytes.Length - 22;
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(end + 20), 4);
        AssertArchive([.. bytes, (byte)'t', (byte)'e', (byte)'s', (byte)'t'], null, "win-x64");
    }

    [Theory]
    [InlineData(false, 0x00000010u)]
    [InlineData(false, 0x00000008u)]
    [InlineData(false, 0x00000040u)]
    [InlineData(false, 0x00000400u)]
    [InlineData(false, 0x40000000u)]
    [InlineData(false, 0xa0000000u)]
    [InlineData(false, 0x20000000u)]
    [InlineData(true, 0x80000000u)]
    [InlineData(true, 0x00000040u)]
    [InlineData(true, 0xa0000000u)]
    public void RejectsContradictoryAndSpecialFileAttributes(bool directory, uint attributes)
    {
        var entries = AgentPackageCorpus.Entries();
        var name = directory ? "Agent/empty/" : "Update-Agent.ps1";
        if (directory) entries.Add((name, []));
        var bytes = AgentPackageCorpus.Mutate((b, _, central) =>
            BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(central + 38), attributes), name, AgentPackageCorpus.Build(entries));
        AssertArchive(bytes, "structure");
    }

    [Fact]
    public void MatchingUnixRegularFileAndDirectoryAttributesAreAccepted()
    {
        var entries = AgentPackageCorpus.Entries();
        entries.Add(("Agent/empty/", []));
        var bytes = AgentPackageCorpus.Build(entries);
        bytes = AgentPackageCorpus.Mutate((b, _, central) =>
            BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(central + 38), 0x81a40020), "Update-Agent.ps1", bytes);
        bytes = AgentPackageCorpus.Mutate((b, _, central) =>
            BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(central + 38), 0x41ed0010), "Agent/empty/", bytes);
        AssertArchive(bytes, null, "win-x64");
    }

    [Fact]
    public void DirectoryEntriesCannotContainData()
    {
        var entries = AgentPackageCorpus.Entries();
        entries.Add(("Agent/empty/", AgentPackageCorpus.Utf8("data")));
        AssertArchive(AgentPackageCorpus.Build(entries), "structure");
    }

    [Fact]
    public void ExtractedTreeMustMatchDirectoriesAndFileBytes()
    {
        var archive = AgentPackageCorpus.Valid();
        AssertArchive(archive, null, "win-x64");
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
        finally { AgentPackageCorpus.DeleteOwnedTemp(root, "kr-package-tree-"); }
    }

    [Fact]
    public void ArchiveExtractionAndExtractedValidationAgreeOnCanonicalInventory()
    {
        var archive = AgentPackageCorpus.Valid();
        AssertArchive(archive, null, "win-x64");
        var expected = PackageVerifier.ValidateArchive(new MemoryStream(archive), AgentPackageCorpus.Version);
        Assert.Equal(13, expected.Nodes.Count);
        var script = Assert.Single(expected.Nodes, n => n.Path == "Update-Agent.ps1");
        Assert.False(script.IsDirectory);
        Assert.Equal(15, script.Length);
        Assert.Equal("bef219c9370f2e928131315e92c93f03ad97947ab843ca2d3a4c6bb3507d792b", script.Sha256);
        Assert.Contains(expected.Nodes, n => n.Path == "Agent" && n.IsDirectory && n.Length == 0 && n.Sha256 is null);
        var root = Path.Combine(Path.GetTempPath(), "kr-package-tree-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var extracted = PackageVerifier.Extract(new MemoryStream(archive), root, expected);
            var validated = PackageVerifier.ValidateExtracted(root, expected);
            Assert.Equal(expected.ArchiveHash, extracted.ArchiveHash);
            Assert.Equal(expected.TargetRuntime, extracted.TargetRuntime);
            Assert.Equal(expected.TotalBytes, extracted.TotalBytes);
            Assert.Equal(expected.Nodes, extracted.Nodes);
            Assert.Equal(expected.InventoryDigest, extracted.InventoryDigest);
            Assert.Equal(expected.Nodes, validated.Nodes);
            Assert.Equal(expected.InventoryDigest, validated.InventoryDigest);
            Assert.Equal("# inert fixture", File.ReadAllText(Path.Combine(root, "Update-Agent.ps1")));
        }
        finally { AgentPackageCorpus.DeleteOwnedTemp(root, "kr-package-tree-"); }
    }

    [Fact]
    public void ArchiveAndExtractionAcceptNonSeekableShortReadStreams()
    {
        var archive = AgentPackageCorpus.Valid();
        AssertArchive(archive, null, "win-x64");
        using var validationStream = new NonSeekableReadStream(archive);
        var expected = PackageVerifier.ValidateArchive(validationStream, AgentPackageCorpus.Version);
        var root = Path.Combine(Path.GetTempPath(), "kr-package-tree-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            using var extractionStream = new NonSeekableReadStream(archive);
            var extracted = PackageVerifier.Extract(extractionStream, root, expected);
            Assert.Equal(expected.InventoryDigest, extracted.InventoryDigest);
            Assert.Equal(expected.Nodes, PackageVerifier.ValidateExtracted(root, expected).Nodes);
        }
        finally { AgentPackageCorpus.DeleteOwnedTemp(root, "kr-package-tree-"); }
    }

    private sealed class NonSeekableReadStream(byte[] bytes) : Stream
    {
        private readonly MemoryStream inner = new(bytes, false);
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, Math.Min(count, 113));
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        protected override void Dispose(bool disposing) { if (disposing) inner.Dispose(); base.Dispose(disposing); }
    }

    [Theory]
    [InlineData("")]
    [InlineData("other-command")]
    [InlineData("validate-archive")]
    public void CliRejectsUnsupportedCommandsAndIncompleteArguments(string command)
    {
        var arguments = command.Length == 0 ? new[] { Cli.Value } : new[] { Cli.Value, command };
        var (code, output) = Run("dotnet", arguments);
        Assert.Equal(2, code);
        Assert.True(output.Length <= 256);
    }

    [Fact]
    public void CliRejectsDuplicateOrUnknownFlagsAndDoesNotOverwriteOutput()
    {
        var root = Path.Combine(Path.GetTempPath(), "kr-package-cli-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var archive = Path.Combine(root, "package.zip");
            var output = Path.Combine(root, "result.json");
            File.WriteAllBytes(archive, AgentPackageCorpus.Valid());
            File.WriteAllText(output, "preserve");
            var (duplicate, _) = Run("dotnet", [Cli.Value, "validate-archive", "--archive", archive,
                "--archive", archive, "--output-json", output]);
            var (unknown, _) = Run("dotnet", [Cli.Value, "validate-archive", "--archive", archive,
                "--version", AgentPackageCorpus.Version, "--extra", output]);
            var (existing, _) = Run("dotnet", [Cli.Value, "validate-archive", "--archive", archive,
                "--version", AgentPackageCorpus.Version, "--output-json", output]);
            Assert.Equal(2, duplicate);
            Assert.Equal(2, unknown);
            Assert.Equal(2, existing);
            Assert.Equal("preserve", File.ReadAllText(output));
        }
        finally { AgentPackageCorpus.DeleteOwnedTemp(root, "kr-package-cli-"); }
    }

    [Fact]
    public void CliReportsUnreadableArchiveAsBoundedTypedIoResult()
    {
        var root = Path.Combine(Path.GetTempPath(), "kr-package-cli-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var output = Path.Combine(root, "result.json");
            var (code, _) = Run("dotnet", [Cli.Value, "validate-archive", "--archive", Path.Combine(root, "missing.zip"),
                "--version", AgentPackageCorpus.Version, "--output-json", output]);
            Assert.Equal(1, code);
            var bytes = File.ReadAllBytes(output);
            Assert.InRange(bytes.Length, 1, 256);
            using var json = JsonDocument.Parse(bytes);
            Assert.Equal("rejected", json.RootElement.GetProperty("status").GetString());
            Assert.Equal("io", json.RootElement.GetProperty("category").GetString());
            Assert.Equal(JsonValueKind.Null, json.RootElement.GetProperty("runtime").ValueKind);
        }
        finally { AgentPackageCorpus.DeleteOwnedTemp(root, "kr-package-cli-"); }
    }

    [Fact]
    public void ExplicitAndImplicitDirectoryRepresentationsHaveSameInventoryDigest()
    {
        var implicitArchive = AgentPackageCorpus.Valid();
        var entries = AgentPackageCorpus.Entries();
        entries.Add(("Agent/", []));
        var explicitArchive = AgentPackageCorpus.Build(entries);
        AssertArchive(implicitArchive, null, "win-x64");
        AssertArchive(explicitArchive, null, "win-x64");
        var implicitInventory = PackageVerifier.ValidateArchive(new MemoryStream(implicitArchive), AgentPackageCorpus.Version);
        var explicitInventory = PackageVerifier.ValidateArchive(new MemoryStream(explicitArchive), AgentPackageCorpus.Version);
        Assert.NotEqual(implicitInventory.ArchiveHash, explicitInventory.ArchiveHash);
        Assert.Equal(implicitInventory.Nodes, explicitInventory.Nodes);
        Assert.Equal(implicitInventory.InventoryDigest, explicitInventory.InventoryDigest);
    }

    [Theory]
    [InlineData("missing-file")]
    [InlineData("extra-file")]
    [InlineData("forbidden-extra-file")]
    [InlineData("same-length-mutation")]
    [InlineData("missing-directory")]
    [InlineData("extra-directory")]
    [InlineData("file-replaced-by-directory")]
    [InlineData("directory-replaced-by-file")]
    [InlineData("partial-extraction")]
    public void ExtractedInventoryRejectsMissingExtraMutatedAndPartialNodes(string mutation)
    {
        var entries = AgentPackageCorpus.Entries();
        entries.Add(("Agent/empty/", []));
        entries.Add(("Agent/deep/nested.txt", AgentPackageCorpus.Utf8("nested")));
        var archive = AgentPackageCorpus.Build(entries);
        AssertArchive(archive, null, "win-x64");
        var expected = PackageVerifier.ValidateArchive(new MemoryStream(archive), AgentPackageCorpus.Version);
        var root = Path.Combine(Path.GetTempPath(), "kr-package-tree-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            if (mutation == "partial-extraction")
            {
                Directory.CreateDirectory(Path.Combine(root, "Agent"));
                File.WriteAllBytes(Path.Combine(root, "Update-Agent.ps1"), AgentPackageCorpus.Utf8("# inert fixture"));
            }
            else PackageVerifier.Extract(new MemoryStream(archive), root, expected);
            var file = Path.Combine(root, "Update-Agent.ps1");
            var empty = Path.Combine(root, "Agent", "empty");
            switch (mutation)
            {
                case "missing-file": File.Delete(file); break;
                case "extra-file": File.WriteAllText(Path.Combine(root, "Agent", "unexpected.txt"), "extra"); break;
                case "forbidden-extra-file": File.WriteAllText(Path.Combine(root, "Agent", "appsettings.json"), "{}"); break;
                case "same-length-mutation": File.WriteAllText(file, "# inert fixturE"); break;
                case "missing-directory": Directory.Delete(empty); break;
                case "extra-directory": Directory.CreateDirectory(Path.Combine(root, "Agent", "unexpected")); break;
                case "file-replaced-by-directory": File.Delete(file); Directory.CreateDirectory(file); break;
                case "directory-replaced-by-file": Directory.Delete(empty); File.WriteAllText(empty, "file"); break;
            }
            var error = Assert.Throws<PackageValidationException>(() => PackageVerifier.ValidateExtracted(root, expected));
            Assert.Equal("inventory", error.Category);
            if (mutation == "partial-extraction")
            {
                var extractError = Assert.Throws<PackageValidationException>(() =>
                    PackageVerifier.Extract(new MemoryStream(archive), root, expected));
                Assert.Equal("inventory", extractError.Category);
                Assert.Equal("# inert fixture", File.ReadAllText(file));
            }
        }
        finally { AgentPackageCorpus.DeleteOwnedTemp(root, "kr-package-tree-"); }
    }

    [Fact]
    public void ExtractionRefusesChangedArchiveOrInventoryBeforeWriting()
    {
        var original = AgentPackageCorpus.Valid();
        var changed = AgentPackageCorpus.Replace("Update-Agent.ps1", "# changed fixture");
        var corrupt = AgentPackageCorpus.CrcTamper();
        AssertArchive(original, null, "win-x64");
        AssertArchive(changed, null, "win-x64");
        AssertArchive(corrupt, "structure");
        var expected = PackageVerifier.ValidateArchive(new MemoryStream(original), AgentPackageCorpus.Version);
        var root = Path.Combine(Path.GetTempPath(), "kr-package-tree-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var mismatch = Assert.Throws<PackageValidationException>(() =>
                PackageVerifier.Extract(new MemoryStream(changed), root, expected));
            Assert.Equal("inventory", mismatch.Category);
            var malformed = Assert.Throws<PackageValidationException>(() =>
                PackageVerifier.Extract(new MemoryStream(corrupt), root, expected));
            Assert.Equal("structure", malformed.Category);
            Assert.Empty(Directory.EnumerateFileSystemEntries(root));
        }
        finally { AgentPackageCorpus.DeleteOwnedTemp(root, "kr-package-tree-"); }
    }

    [Fact]
    public void ExtractedTreeRejectsMissingDirectoryAndHardlinkedFile()
    {
        var entries = AgentPackageCorpus.Entries();
        entries.Add(("Agent/empty/", []));
        var archive = AgentPackageCorpus.Build(entries);
        AssertArchive(archive, null, "win-x64");
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
            AgentPackageCorpus.DeleteOwnedTemp(root, "kr-package-tree-");
        }
    }

    [Fact]
    public void ExtractedTreeRejectsJunctionReplacingExpectedDirectory()
    {
        var archive = AgentPackageCorpus.Valid();
        AssertArchive(archive, null, "win-x64");
        var inventory = Invoke("ValidateArchive", new MemoryStream(archive), AgentPackageCorpus.Version)!;
        var root = Path.Combine(Path.GetTempPath(), "kr-package-tree-" + Guid.NewGuid().ToString("N"));
        var target = root + "-target";
        var junction = Path.Combine(root, "Agent");
        Directory.CreateDirectory(root);
        try
        {
            Invoke("Extract", new MemoryStream(archive), root, inventory);
            var ownedRoot = AgentPackageCorpus.OwnedTempPath(root, "kr-package-tree-").TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            var source = Path.GetFullPath(junction);
            if (!source.StartsWith(ownedRoot, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Junction source escaped its owned tree.");
            Directory.Move(source, AgentPackageCorpus.OwnedTempPath(target, "kr-package-tree-"));
            var (code, output) = Run("cmd.exe", ["/d", "/c", "mklink", "/J", junction, target]);
            Assert.True(code == 0, output);
            AssertCategory("inventory", () => Invoke("ValidateExtracted", root, inventory));
        }
        finally
        {
            if (Directory.Exists(junction)) Directory.Delete(junction);
            AgentPackageCorpus.DeleteOwnedTemp(target, "kr-package-tree-");
            AgentPackageCorpus.DeleteOwnedTemp(root, "kr-package-tree-");
        }
    }

    [Fact]
    public void ExtractedRootCannotBeAReparsePoint()
    {
        var archive = AgentPackageCorpus.Valid();
        AssertArchive(archive, null, "win-x64");
        var expected = PackageVerifier.ValidateArchive(new MemoryStream(archive), AgentPackageCorpus.Version);
        var root = Path.Combine(Path.GetTempPath(), "kr-package-tree-" + Guid.NewGuid().ToString("N"));
        var target = root + "-target";
        Directory.CreateDirectory(target);
        try
        {
            PackageVerifier.Extract(new MemoryStream(archive), target, expected);
            var (code, output) = Run("cmd.exe", ["/d", "/c", "mklink", "/J", root, target]);
            Assert.True(code == 0, output);
            var error = Assert.Throws<PackageValidationException>(() => PackageVerifier.ValidateExtracted(root, expected));
            Assert.Equal("inventory", error.Category);
        }
        finally
        {
            var safeRoot = AgentPackageCorpus.OwnedTempPath(root, "kr-package-tree-");
            if (Directory.Exists(safeRoot)) Directory.Delete(safeRoot);
            AgentPackageCorpus.DeleteOwnedTemp(target, "kr-package-tree-");
        }
    }

    [Fact]
    public void ExtractionRequiresEmptyRootAndChecksTheWindowsPathBound()
    {
        var archive = AgentPackageCorpus.Valid();
        AssertArchive(archive, null, "win-x64");
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
            var tooLong = root + "x";
            Directory.CreateDirectory(tooLong);
            AssertCategory("inventory", () => Invoke("Extract", new MemoryStream(archive), tooLong, inventory));
            Directory.Delete(tooLong);
            File.WriteAllText(Path.Combine(root, "preexisting"), "keep");
            AssertCategory("inventory", () => Invoke("Extract", new MemoryStream(archive), root, inventory));
            File.Delete(Path.Combine(root, "preexisting"));
            Invoke("Extract", new MemoryStream(archive), root, inventory);
            Invoke("ValidateExtracted", root, inventory);
        }
        finally { AgentPackageCorpus.DeleteOwnedTemp(basePath, "kr-package-long-"); }
    }

    [Fact]
    public void DriveRootIsRejectedOrRemainsFullyQualifiedAfterNormalization()
    {
        var expected = PackageVerifier.ValidateArchive(new MemoryStream(AgentPackageCorpus.Valid()), AgentPackageCorpus.Version);
        var driveRoot = Path.GetPathRoot(Path.GetTempPath())!;
        Assert.True(Path.IsPathFullyQualified(driveRoot));
        var method = typeof(PackageVerifier).GetMethod("CheckRoot", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);
        string? normalized = null;
        var error = Record.Exception(() => normalized = (string)method.Invoke(null, [driveRoot, expected])!);
        if (error is TargetInvocationException { InnerException: PackageValidationException validation })
            Assert.Equal("inventory", validation.Category);
        else
        {
            Assert.Null(error);
            Assert.True(Path.IsPathFullyQualified(normalized!), $"Normalized root must remain absolute: {normalized}");
            Assert.Equal(Path.GetFullPath(driveRoot), Path.GetFullPath(normalized!), ignoreCase: true);
        }
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
            AgentPackageCorpus.DeleteOwnedTemp(root, "kr-package-sentinel-");
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
            var jsonBytes = File.ReadAllBytes(output);
            Assert.InRange(jsonBytes.Length, 1, 256);
            using var json = JsonDocument.Parse(jsonBytes);
            Assert.Equal(new[] { "category", "runtime", "status" }, json.RootElement.EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal).ToArray());
            Assert.Equal(category is null ? 0 : 1, code);
            Assert.Equal(category is null ? "accepted" : "rejected", json.RootElement.GetProperty("status").GetString());
            Assert.Equal(category, json.RootElement.GetProperty("category").GetString());
            Assert.Equal(runtime, json.RootElement.GetProperty("runtime").GetString());
        }
        finally { AgentPackageCorpus.DeleteOwnedTemp(root, "kr-package-cli-"); }
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

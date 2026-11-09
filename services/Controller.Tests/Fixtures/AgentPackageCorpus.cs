using System.Buffers.Binary;
using System.Diagnostics;
using System.IO.Compression;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using System.Text;

namespace Controller.Tests.Fixtures;

internal static class AgentPackageCorpus
{
    internal const string Version = "2.3.4";
    internal const string Manifest = """{"packageFormatVersion":1,"agentInformationalVersion":"2.3.4","sourceCommit":"0123456789ab","sourceDirty":false,"releaseBuild":true,"agentUpdateChallengeProtocol":2}""";
    internal const string Deps = """{"libraries":{"Agent/2.3.4":{"type":"project"}}}""";
    internal static readonly string[] Scripts = ["Install-AgentPackage.ps1", "Install-Service.ps1", "Install-TightVncServer.ps1", "Prepare-SunshineStreamGate.ps1", "PrivateInstallFiles.ps1", "ServiceInstallTransaction.ps1", "Update-AgentPackage.ps1", "Update-Agent.ps1"];
    private static readonly Lazy<Dictionary<string, (byte[] Exe, byte[] Dll)>> Published = new(Publish);
    internal static byte[] Utf8(string value) => Encoding.UTF8.GetBytes(value);

    internal static string OwnedTempPath(string path, string prefix)
    {
        var resolved = Path.GetFullPath(path);
        var temp = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!resolved.StartsWith(temp, StringComparison.OrdinalIgnoreCase)
            || !Path.GetFileName(resolved).StartsWith(prefix, StringComparison.Ordinal))
            throw new InvalidOperationException("Refusing to change a path outside the owned temporary tree.");
        return resolved;
    }

    internal static void DeleteOwnedTemp(string path, string prefix)
    {
        var resolved = OwnedTempPath(path, prefix);
        if (Directory.Exists(resolved)) Directory.Delete(resolved, true);
    }

    internal static List<(string Name, byte[] Bytes)> Entries(string runtime = "win-x64")
    {
        var binary = Published.Value[runtime];
        var entries = new List<(string, byte[])> {
            ("Agent/Agent.exe", binary.Exe), ("Agent/Agent.dll", binary.Dll),
            ("Agent/Agent.deps.json", Utf8(Deps)), ("AgentPackage.manifest.json", Utf8(Manifest))
        };
        entries.AddRange(Scripts.Select(s => (s, Utf8("# inert fixture"))));
        return entries;
    }

    internal static byte[] Valid(string runtime = "win-x64") => Build(Entries(runtime));

    internal static byte[] Build(IEnumerable<(string Name, byte[] Bytes)> entries, CompressionLevel compression = CompressionLevel.NoCompression, bool descriptor = false)
    {
        using var output = new MemoryStream();
        using var wrapper = descriptor ? new NonSeekableWriteStream(output) : null;
        using (var zip = new ZipArchive(wrapper ?? (Stream)output, ZipArchiveMode.Create, true))
            foreach (var (name, bytes) in entries)
            {
                using var entry = zip.CreateEntry(name, compression).Open();
                entry.Write(bytes);
            }
        return output.ToArray();
    }

    private sealed class NonSeekableWriteStream(Stream target) : Stream
    {
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() => target.Flush();
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => target.Write(buffer, offset, count);
    }

    internal static byte[] Replace(string name, string value)
    {
        var entries = Entries();
        var i = entries.FindIndex(e => e.Name == name);
        entries[i] = (name, Utf8(value));
        return Build(entries);
    }

    internal static byte[] Added(string name, string value = "")
    {
        var entries = Entries(); entries.Add((name, Utf8(value))); return Build(entries);
    }

    internal static byte[] Mutate(Action<byte[], int, int> action, string name = "Update-Agent.ps1", byte[]? archive = null)
    {
        var bytes = archive ?? Valid();
        var eocd = bytes.Length - 22;
        var central = (int)BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(eocd + 16));
        while (central < eocd)
        {
            var nameLength = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(central + 28));
            var extraLength = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(central + 30));
            var commentLength = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(central + 32));
            if (Encoding.ASCII.GetString(bytes, central + 46, nameLength) == name)
            {
                var local = (int)BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(central + 42));
                action(bytes, local, central); return bytes;
            }
            central += 46 + nameLength + extraLength + commentLength;
        }
        throw new InvalidOperationException("Entry not found");
    }

    internal static byte[] CrcTamper() => Mutate((b, local, _) =>
    {
        var name = BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan(local + 26));
        var extra = BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan(local + 28));
        b[local + 30 + name + extra] ^= 1;
    });

    internal static byte[] AlterDeflate(bool append)
    {
        var bytes = Build(Entries(), CompressionLevel.Optimal);
        var eocd = bytes.Length - 22;
        var cdStart = (int)BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(eocd + 16));
        var central = cdStart;
        while (true)
        {
            var nameLength = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(central + 28));
            var extraLength = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(central + 30));
            var commentLength = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(central + 32));
            if (Encoding.ASCII.GetString(bytes, central + 46, nameLength) == "Update-Agent.ps1") break;
            central += 46 + nameLength + extraLength + commentLength;
        }
        var local = (int)BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(central + 42));
        var compressed = (int)BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(central + 20));
        var start = local + 30 + BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(local + 26))
            + BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(local + 28));
        var end = start + compressed;
        if (end != cdStart) throw new InvalidOperationException("Expected last entry before central directory.");
        var delta = append ? 1 : -1;
        var result = new byte[bytes.Length + delta];
        Array.Copy(bytes, 0, result, 0, end + (append ? 0 : -1));
        Array.Copy(bytes, end, result, end + delta, bytes.Length - end);
        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(local + 18), (uint)(compressed + delta));
        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(central + delta + 20), (uint)(compressed + delta));
        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(eocd + delta + 16), (uint)(cdStart + delta));
        return result;
    }

    internal static byte[] DeflateStoredBlock(string variant)
    {
        var bytes = Valid();
        var end = bytes.Length - 22;
        var centralStart = (int)BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(end + 16));
        var central = centralStart;
        while (true)
        {
            var nameLength = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(central + 28));
            var extraLength = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(central + 30));
            var commentLength = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(central + 32));
            if (Encoding.ASCII.GetString(bytes, central + 46, nameLength) == "Update-Agent.ps1") break;
            central += 46 + nameLength + extraLength + commentLength;
        }
        var local = (int)BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(central + 42));
        var start = local + 30 + BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(local + 26))
            + BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(local + 28));
        var length = (int)BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(central + 24));
        if (BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(central + 10)) != 0 || start + length != centralStart || length > ushort.MaxValue)
            throw new InvalidOperationException("Expected final stored fixture entry.");
        var block = new byte[length + 5];
        block[0] = variant == "reserved-block" ? (byte)7 : (byte)1;
        BinaryPrimitives.WriteUInt16LittleEndian(block.AsSpan(1), (ushort)length);
        BinaryPrimitives.WriteUInt16LittleEndian(block.AsSpan(3), (ushort)~length);
        if (variant == "bad-complement") block[3] ^= 1;
        Array.Copy(bytes, start, block, 5, length);
        var result = new byte[bytes.Length + 5];
        Array.Copy(bytes, 0, result, 0, start);
        Array.Copy(block, 0, result, start, block.Length);
        Array.Copy(bytes, centralStart, result, centralStart + 5, bytes.Length - centralStart);
        BinaryPrimitives.WriteUInt16LittleEndian(result.AsSpan(local + 8), 8);
        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(local + 18), (uint)block.Length);
        BinaryPrimitives.WriteUInt16LittleEndian(result.AsSpan(central + 5 + 10), 8);
        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(central + 5 + 20), (uint)block.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(end + 5 + 16), (uint)(centralStart + 5));
        return result;
    }

    internal static byte[] Descriptor(bool corrupt)
    {
        var bytes = Build(Entries(), CompressionLevel.Optimal, true);
        if (!corrupt) return bytes;
        var end = bytes.Length - 22;
        var central = (int)BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(end + 16));
        var local = (int)BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(central + 42));
        var start = local + 30 + BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(local + 26))
            + BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(local + 28));
        var size = (int)BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(central + 20));
        var descriptor = start + size;
        if (BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(descriptor)) != 0x08074b50)
            throw new InvalidOperationException("Expected signed descriptor.");
        bytes[descriptor + 4] ^= 1;
        return bytes;
    }

    internal static byte[] DescriptorVariant(string variant)
    {
        var bytes = Build(Entries(), CompressionLevel.Optimal, true);
        var end = bytes.Length - 22;
        var central = (int)BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(end + 16));
        var local = (int)BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(central + 42));
        var start = local + 30 + BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(local + 26))
            + BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(local + 28));
        var size = (int)BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(central + 20));
        var descriptor = start + size;
        if (BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(descriptor)) != 0x08074b50)
            throw new InvalidOperationException("Expected signed descriptor.");
        if (variant.StartsWith("unsigned", StringComparison.Ordinal))
        {
            var shortened = new byte[bytes.Length - 4];
            Array.Copy(bytes, 0, shortened, 0, descriptor);
            Array.Copy(bytes, descriptor + 4, shortened, descriptor, bytes.Length - descriptor - 4);
            bytes = shortened;
            end -= 4;
            central -= 4;
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(end + 16), (uint)central);
            var entry = central;
            while (entry < end)
            {
                var oldLocal = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(entry + 42));
                if (oldLocal > local) BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(entry + 42), oldLocal - 4);
                entry += 46 + BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(entry + 28))
                    + BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(entry + 30))
                    + BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(entry + 32));
            }
            if (variant == "unsigned-crc") bytes[descriptor] ^= 1;
            return bytes;
        }
        switch (variant)
        {
            case "signed-crc": bytes[descriptor + 4] ^= 1; break;
            case "signed-compressed": bytes[descriptor + 8] ^= 1; break;
            case "signed-expanded": bytes[descriptor + 12] ^= 1; break;
            case "local-crc": bytes[local + 14] ^= 1; break;
            case "local-compressed": bytes[local + 18] ^= 1; break;
            case "local-expanded": bytes[local + 22] ^= 1; break;
            default: throw new ArgumentOutOfRangeException(nameof(variant));
        }
        return bytes;
    }

    internal static byte[] CentralExtra(ushort id, byte[] value)
        => CentralExtras((id, value));

    internal static byte[] CentralExtras(params (ushort Id, byte[] Value)[] extras)
    {
        var bytes = Valid();
        var end = bytes.Length - 22;
        var central = (int)BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(end + 16));
        var nameLength = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(central + 28));
        var at = central + 46 + nameLength;
        var extraLength = extras.Sum(e => 4 + e.Value.Length);
        var result = new byte[bytes.Length + extraLength];
        Array.Copy(bytes, 0, result, 0, at);
        var cursor = at;
        foreach (var (id, value) in extras)
        {
            BinaryPrimitives.WriteUInt16LittleEndian(result.AsSpan(cursor), id);
            BinaryPrimitives.WriteUInt16LittleEndian(result.AsSpan(cursor + 2), (ushort)value.Length);
            value.CopyTo(result, cursor + 4);
            cursor += 4 + value.Length;
        }
        Array.Copy(bytes, at, result, cursor, bytes.Length - at);
        BinaryPrimitives.WriteUInt16LittleEndian(result.AsSpan(central + 30), (ushort)extraLength);
        var size = BinaryPrimitives.ReadUInt32LittleEndian(result.AsSpan(end + extraLength + 12));
        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(end + extraLength + 12), size + (uint)extraLength);
        return result;
    }

    internal static byte[] ContradictoryCrossTimestamp(long ntfsModified = 116444736010000000L)
    {
        var bytes = CentralExtra(0x5455, [1, 0, 0, 0, 0]);
        var oldEnd = bytes.Length - 22;
        var oldCentral = (int)BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(oldEnd + 16));
        var nameLength = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(26));
        var at = 30 + nameLength;
        var result = new byte[bytes.Length + 36];
        Array.Copy(bytes, 0, result, 0, at);
        BinaryPrimitives.WriteUInt16LittleEndian(result.AsSpan(at), 0x000a);
        BinaryPrimitives.WriteUInt16LittleEndian(result.AsSpan(at + 2), 32);
        BinaryPrimitives.WriteUInt16LittleEndian(result.AsSpan(at + 8), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(result.AsSpan(at + 10), 24);
        BinaryPrimitives.WriteInt64LittleEndian(result.AsSpan(at + 12), ntfsModified);
        BinaryPrimitives.WriteInt64LittleEndian(result.AsSpan(at + 20), 116444736000000000L);
        BinaryPrimitives.WriteInt64LittleEndian(result.AsSpan(at + 28), 116444736000000000L);
        Array.Copy(bytes, at, result, at + 36, bytes.Length - at);
        BinaryPrimitives.WriteUInt16LittleEndian(result.AsSpan(28), 36);
        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(oldEnd + 36 + 16), (uint)(oldCentral + 36));
        var central = oldCentral + 36;
        while (central < oldEnd + 36)
        {
            var local = BinaryPrimitives.ReadUInt32LittleEndian(result.AsSpan(central + 42));
            if (local > 0) BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(central + 42), local + 36);
            central += 46 + BinaryPrimitives.ReadUInt16LittleEndian(result.AsSpan(central + 28))
                + BinaryPrimitives.ReadUInt16LittleEndian(result.AsSpan(central + 30))
                + BinaryPrimitives.ReadUInt16LittleEndian(result.AsSpan(central + 32));
        }
        return result;
    }

    internal static byte[] ManyNodes(int added, bool sharedParent = false)
    {
        var entries = Entries();
        for (var i = 0; i < added; i++) entries.Add(($"Agent/{(sharedParent ? "shared/" : "")}f{i:0000}", []));
        return Build(entries);
    }

    internal static byte[] ExpandedTo(long expandedLength)
    {
        var entries = Entries();
        var remaining = expandedLength - entries.Sum(e => (long)e.Bytes.Length);
        using var output = new MemoryStream();
        using (var zip = new ZipArchive(output, ZipArchiveMode.Create, true))
        {
            foreach (var (name, bytes) in entries)
            {
                using var entry = zip.CreateEntry(name, CompressionLevel.Optimal).Open();
                entry.Write(bytes);
            }
            using var filler = zip.CreateEntry("Agent/filler", CompressionLevel.Optimal).Open();
            var zeros = new byte[65536];
            while (remaining > 0)
            {
                var count = (int)Math.Min(remaining, zeros.Length);
                filler.Write(zeros, 0, count);
                remaining -= count;
            }
        }
        return output.ToArray();
    }

    internal static byte[] CompressedTo(int archiveLength)
    {
        var entries = Entries();
        entries.Add(("Agent/filler", []));
        var overhead = Build(entries).Length;
        entries[^1] = ("Agent/filler", new byte[archiveLength - overhead]);
        var result = Build(entries);
        if (result.Length != archiveLength) throw new InvalidOperationException("Unexpected ZIP overhead.");
        return result;
    }

    internal static byte[] InvalidUtf8InDeps()
    {
        var entries = Entries();
        var i = entries.FindIndex(e => e.Name == "Agent/Agent.deps.json");
        var data = Utf8("""{"libraries":{"Agent/2.3.4":{"type":"project"}},"unused":"X"}""");
        data[Array.IndexOf(data, (byte)'X')] = 0x80;
        entries[i] = ("Agent/Agent.deps.json", data);
        return Build(entries);
    }

    internal static byte[] DuplicateProtocolAssembly()
    {
        var root = Path.Combine(Path.GetTempPath(), "kr-duplicate-metadata-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            File.WriteAllText(Path.Combine(root, "Fixture.csproj"), """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup><TargetFramework>net10.0</TargetFramework><AssemblyName>Agent</AssemblyName><GenerateAssemblyInfo>false</GenerateAssemblyInfo></PropertyGroup>
                </Project>
                """);
            File.WriteAllText(Path.Combine(root, "Fixture.cs"), """
                using System.Reflection;
                [assembly: AssemblyInformationalVersion("2.3.4")]
                [assembly: AssemblyMetadata("AgentUpdateChallengeProtocol", "3")]
                [assembly: AssemblyMetadata("AgentUpdateChallengeProtocol", "2")]
                """);
            var start = new ProcessStartInfo("dotnet") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
            foreach (var arg in new[] { "build", Path.Combine(root, "Fixture.csproj"), "-c", "Release", "-o", Path.Combine(root, "output") }) start.ArgumentList.Add(arg);
            using var process = Process.Start(start)!;
            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();
            process.WaitForExit();
            if (process.ExitCode != 0) throw new InvalidOperationException(stdout.Result + stderr.Result);
            var assembly = File.ReadAllBytes(Path.Combine(root, "output", "Agent.dll"));
            var key = Utf8("AgentUpdateChallengeProtocol");
            byte[] blob = [1, 0, (byte)key.Length, .. key, 1, (byte)'3', 0, 0];
            var offset = assembly.AsSpan().IndexOf(blob);
            if (offset < 0) throw new InvalidOperationException("Expected metadata blob is missing.");
            assembly[offset + 3 + key.Length + 1] = (byte)'2';
            return assembly;
        }
        finally { DeleteOwnedTemp(root, "kr-duplicate-metadata-"); }
    }

    internal static byte[] DuplicateInformationalVersionAssembly()
    {
        var root = Path.Combine(Path.GetTempPath(), "kr-duplicate-version-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            File.WriteAllText(Path.Combine(root, "Fixture.csproj"), """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup><TargetFramework>net10.0</TargetFramework><AssemblyName>Agent</AssemblyName><GenerateAssemblyInfo>false</GenerateAssemblyInfo></PropertyGroup>
                </Project>
                """);
            File.WriteAllText(Path.Combine(root, "Fixture.cs"), """
                using System.Reflection;
                [assembly: AssemblyInformationalVersion("2.3.4")]
                [assembly: AssemblyFileVersion("2.3.4")]
                [assembly: AssemblyMetadata("AgentUpdateChallengeProtocol", "2")]
                """);
            var start = new ProcessStartInfo("dotnet") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
            foreach (var arg in new[] { "build", Path.Combine(root, "Fixture.csproj"), "-c", "Release", "-o", Path.Combine(root, "output") }) start.ArgumentList.Add(arg);
            using var process = Process.Start(start)!;
            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();
            process.WaitForExit();
            if (process.ExitCode != 0) throw new InvalidOperationException(stdout.Result + stderr.Result);
            var assembly = File.ReadAllBytes(Path.Combine(root, "output", "Agent.dll"));
            DuplicateVersionMetadataRow(assembly);
            return assembly;
        }
        finally { DeleteOwnedTemp(root, "kr-duplicate-version-"); }
    }

    private static void DuplicateVersionMetadataRow(byte[] assembly)
    {
        int metadataStart, informationalRow = 0, fileVersionRow = 0;
        using (var pe = new PEReader(new MemoryStream(assembly)))
        {
            metadataStart = pe.PEHeaders.MetadataStartOffset;
            var metadata = pe.GetMetadataReader();
            foreach (var handle in metadata.GetAssemblyDefinition().GetCustomAttributes())
            {
                var constructor = metadata.GetCustomAttribute(handle).Constructor;
                if (constructor.Kind != HandleKind.MemberReference) continue;
                var parent = metadata.GetMemberReference((MemberReferenceHandle)constructor).Parent;
                if (parent.Kind != HandleKind.TypeReference) continue;
                var name = metadata.GetString(metadata.GetTypeReference((TypeReferenceHandle)parent).Name);
                if (name == "AssemblyInformationalVersionAttribute") informationalRow = MetadataTokens.GetRowNumber(handle);
                if (name == "AssemblyFileVersionAttribute") fileVersionRow = MetadataTokens.GetRowNumber(handle);
            }
        }
        if (informationalRow == 0 || fileVersionRow == 0) throw new InvalidOperationException("Fixture metadata attributes are missing.");
        var span = assembly.AsSpan();
        var cursor = metadataStart + 16 + checked((int)BinaryPrimitives.ReadUInt32LittleEndian(span[(metadataStart + 12)..]));
        cursor = (cursor + 3) & ~3;
        var streams = BinaryPrimitives.ReadUInt16LittleEndian(span[(cursor + 2)..]);
        cursor += 4;
        var tables = 0;
        for (var i = 0; i < streams; i++)
        {
            var offset = BinaryPrimitives.ReadUInt32LittleEndian(span[cursor..]);
            var nameStart = cursor + 8;
            var end = nameStart;
            while (span[end] != 0) end++;
            var name = Encoding.ASCII.GetString(span[nameStart..end]);
            if (name is "#~" or "#-") tables = checked(metadataStart + (int)offset);
            cursor = (end + 4) & ~3;
        }
        if (tables == 0 || (span[tables + 6] & 7) != 0) throw new InvalidOperationException("Fixture metadata heaps are unexpectedly large.");
        var valid = BinaryPrimitives.ReadUInt64LittleEndian(span[(tables + 8)..]);
        var rows = new uint[64];
        cursor = tables + 24;
        for (var table = 0; table < 64; table++)
            if ((valid & (1UL << table)) != 0)
            {
                rows[table] = BinaryPrimitives.ReadUInt32LittleEndian(span[cursor..]);
                if (rows[table] >= 2048) throw new InvalidOperationException("Fixture metadata tables are unexpectedly large.");
                cursor += 4;
            }
        int[] rowSizes = [10, 6, 14, 2, 6, 2, 14, 2, 6, 4, 6, 6];
        for (var table = 0; table < rowSizes.Length; table++) cursor += checked((int)rows[table] * rowSizes[table]);
        if ((uint)informationalRow > rows[12] || (uint)fileVersionRow > rows[12]) throw new InvalidOperationException("Invalid fixture metadata row.");
        span.Slice(cursor + (informationalRow - 1) * 6, 6).CopyTo(span.Slice(cursor + (fileVersionRow - 1) * 6, 6));
    }

    private static Dictionary<string, (byte[], byte[])> Publish()
    {
        var project = typeof(AgentPackageCorpus).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
            .Single(a => a.Key == "ControllerProjectPath").Value!;
        var fixture = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(project)!, "..", "Controller.Tests", "Fixtures", "AgentPackageProtocolTwo", "AgentPackageProtocolTwo.csproj"));
        var outputRoot = Path.Combine(Path.GetTempPath(), "kr-package-fixture-" + Guid.NewGuid().ToString("N"));
        try
        {
            var result = new Dictionary<string, (byte[], byte[])>();
            foreach (var runtime in new[] { "win-x64", "win-arm64" })
            {
                var output = Path.Combine(outputRoot, runtime);
                var start = new ProcessStartInfo("dotnet") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
                foreach (var arg in new[] { "publish", fixture, "-c", "Release", "-r", runtime, "--self-contained", "false", "-o", output }) start.ArgumentList.Add(arg);
                using var process = Process.Start(start)!;
                var stdout = process.StandardOutput.ReadToEndAsync();
                var stderr = process.StandardError.ReadToEndAsync();
                process.WaitForExit();
                if (process.ExitCode != 0) throw new InvalidOperationException($"Fixture publish failed ({runtime}): {stdout.Result} {stderr.Result}");
                result.Add(runtime, (File.ReadAllBytes(Path.Combine(output, "Agent.exe")), File.ReadAllBytes(Path.Combine(output, "Agent.dll"))));
            }
            return result;
        }
        finally { DeleteOwnedTemp(outputRoot, "kr-package-fixture-"); }
    }
}

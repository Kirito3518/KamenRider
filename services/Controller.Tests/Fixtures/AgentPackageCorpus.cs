using System.Buffers.Binary;
using System.Diagnostics;
using System.IO.Compression;
using System.Reflection;
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

    internal static byte[] CentralExtra(ushort id, byte[] value)
    {
        var bytes = Valid();
        var end = bytes.Length - 22;
        var central = (int)BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(end + 16));
        var nameLength = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(central + 28));
        var at = central + 46 + nameLength;
        var result = new byte[bytes.Length + 4 + value.Length];
        Array.Copy(bytes, 0, result, 0, at);
        BinaryPrimitives.WriteUInt16LittleEndian(result.AsSpan(at), id);
        BinaryPrimitives.WriteUInt16LittleEndian(result.AsSpan(at + 2), (ushort)value.Length);
        value.CopyTo(result, at + 4);
        Array.Copy(bytes, at, result, at + 4 + value.Length, bytes.Length - at);
        BinaryPrimitives.WriteUInt16LittleEndian(result.AsSpan(central + 30), (ushort)(4 + value.Length));
        var size = BinaryPrimitives.ReadUInt32LittleEndian(result.AsSpan(end + 4 + value.Length + 12));
        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(end + 4 + value.Length + 12), size + (uint)(4 + value.Length));
        return result;
    }

    internal static byte[] ContradictoryCrossTimestamp()
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
        BinaryPrimitives.WriteInt64LittleEndian(result.AsSpan(at + 12), 116444736010000000L);
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
        finally { Directory.Delete(root, true); }
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
        finally { if (Directory.Exists(outputRoot)) Directory.Delete(outputRoot, true); }
    }
}

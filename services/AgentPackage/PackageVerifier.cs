using System.IO.Compression;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Win32.SafeHandles;

namespace KamenRaider.AgentPackage;

public static class PackageVerifier
{
    private const int MaxArchive = 104857600;
    private const long MaxExpanded = 524288000;
    private static readonly string[] Required =
    [
        "Agent/Agent.exe", "Agent/Agent.dll", "Agent/Agent.deps.json", "AgentPackage.manifest.json",
        "Install-AgentPackage.ps1", "Install-Service.ps1", "Install-TightVncServer.ps1",
        "Prepare-SunshineStreamGate.ps1", "PrivateInstallFiles.ps1", "ServiceInstallTransaction.ps1",
        "Update-AgentPackage.ps1", "Update-Agent.ps1"
    ];
    private static readonly HashSet<string> RootFiles = new(Required.Where(s => !s.Contains('/')), StringComparer.OrdinalIgnoreCase);
    private static readonly Regex VersionPattern = new(@"\A(?:[0-9]+\.[0-9]+\.[0-9]+|1\.0\.0\+g[0-9a-f]{12,40})\z", RegexOptions.CultureInvariant);
    private static readonly Regex CommitPattern = new(@"\A[0-9a-f]{12}\z", RegexOptions.CultureInvariant);
    private static readonly Regex DevicePattern = new(@"\A(?:CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])\z", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private static readonly uint[] CrcTable = BuildCrcTable();

    private sealed record Item(string Path, bool Directory, ZipStructureReader.Entry? Entry);
    private sealed record Analysis(PackageInventory Inventory, IReadOnlyList<Item> Items);

    public static PackageInventory ValidateArchive(Stream archive, string requestedVersion)
        => Analyze(ReadArchive(archive), requestedVersion).Inventory;

    public static PackageInventory Extract(Stream archive, string freshOwnedRoot, PackageInventory expected)
    {
        ArgumentNullException.ThrowIfNull(expected);
        var bytes = ReadArchive(archive);
        // The expected inventory was produced using this exact version and archive.
        var analysis = Analyze(bytes, expected.Version);
        MatchInventory(analysis.Inventory, expected);
        var root = CheckRoot(freshOwnedRoot, expected);
        var directoryHandles = new List<SafeFileHandle>();
        try
        {
            directoryHandles.Add(OpenNode(root, true));
            if (Directory.EnumerateFileSystemEntries(root).Any()) throw InventoryError();
            foreach (var item in analysis.Items.Where(i => i.Directory).OrderBy(i => i.Path.Count(c => c == '/')))
            {
                var path = LocalPath(root, item.Path);
                Directory.CreateDirectory(path);
                directoryHandles.Add(OpenNode(path, true));
            }
            foreach (var item in analysis.Items.Where(i => !i.Directory))
            {
                var node = expected.Nodes.Single(n => n.Path == item.Path);
                using var output = new FileStream(LocalPath(root, item.Path), FileMode.CreateNew, FileAccess.Write, FileShare.None);
                var hash = CopyEntry(bytes, item.Entry!, output);
                if (hash != node.Sha256) throw InventoryError();
            }
            return ValidateExtracted(root, expected);
        }
        catch (PackageValidationException) { throw; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        { throw InventoryError(); }
        finally { foreach (var handle in directoryHandles) handle.Dispose(); }
    }

    public static PackageInventory ValidateExtracted(string root, PackageInventory expected)
    {
        ArgumentNullException.ThrowIfNull(expected);
        root = CheckRoot(root, expected);
        var expectedNodes = expected.Nodes.ToDictionary(n => n.Path, StringComparer.Ordinal);
        var observed = new HashSet<string>(StringComparer.Ordinal);
        var handles = new List<SafeFileHandle>();
        long total = 0;
        try
        {
            handles.Add(OpenNode(root, true));
            Visit(root, "");
            if (observed.Count != expectedNodes.Count || total != expected.TotalBytes) throw InventoryError();
            return expected;
        }
        catch (PackageValidationException) { throw; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        { throw InventoryError(); }
        finally { foreach (var handle in handles) handle.Dispose(); }

        void Visit(string directory, string parent)
        {
            foreach (var path in Directory.EnumerateFileSystemEntries(directory))
            {
                var name = Path.GetFileName(path);
                var relative = parent.Length == 0 ? name : parent + "/" + name;
                if (observed.Count >= 5000 || !expectedNodes.TryGetValue(relative, out var node) || !observed.Add(relative))
                    throw InventoryError();
                var handle = OpenNode(path, node.IsDirectory);
                if (node.IsDirectory)
                {
                    handles.Add(handle);
                    Visit(path, relative);
                }
                else
                {
                    using var stream = new FileStream(handle, FileAccess.Read);
                    if (stream.Length != node.Length || stream.Length > MaxExpanded - total) throw InventoryError();
                    total += stream.Length;
                    var hash = Convert.ToHexStringLower(SHA256.HashData(stream));
                    if (hash != node.Sha256) throw InventoryError();
                }
            }
        }
    }

    private static byte[] ReadArchive(Stream archive)
    {
        ArgumentNullException.ThrowIfNull(archive);
        try
        {
            using var output = new MemoryStream();
            var buffer = new byte[65536];
            while (true)
            {
                var read = archive.Read(buffer, 0, buffer.Length);
                if (read == 0) break;
                if (output.Length + read > MaxArchive) throw LimitError();
                output.Write(buffer, 0, read);
            }
            if (output.Length == 0) throw LimitError();
            return output.ToArray();
        }
        catch (PackageValidationException) { throw; }
        catch (Exception e) when (e is IOException or NotSupportedException or ObjectDisposedException)
        { throw new PackageValidationException("structure", "Package stream could not be read."); }
    }

    private static Analysis Analyze(byte[] bytes, string version)
    {
        if (version is null || version.Length > 64 || !VersionPattern.IsMatch(version))
            throw new PackageValidationException("version", "Requested version is invalid.");
        var entries = ZipStructureReader.Read(bytes);
        var nodes = new Dictionary<string, Item>(StringComparer.OrdinalIgnoreCase);
        var explicitPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in entries)
        {
            var directory = entry.Name.EndsWith('/');
            var name = ValidatePath(entry.Name, directory);
            ValidateAttributes(entry, directory);
            if (!explicitPaths.Add(name)) throw PathError();
            var parts = name.Split('/');
            var canonical = "";
            for (var i = 0; i < parts.Length; i++)
            {
                var candidate = canonical.Length == 0 ? parts[i] : canonical + "/" + parts[i];
                var isDirectory = i < parts.Length - 1 || directory;
                if (nodes.TryGetValue(candidate, out var existing))
                {
                    if (!existing.Directory || !isDirectory) throw PathError();
                    canonical = existing.Path;
                    if (i == parts.Length - 1) nodes[canonical] = existing with { Entry = entry };
                }
                else
                {
                    canonical = candidate;
                    nodes.Add(canonical, new Item(canonical, isDirectory, i == parts.Length - 1 ? entry : null));
                    if (nodes.Count > 5000) throw LimitError();
                }
            }
            var root = canonical.Split('/')[0];
            if (!root.Equals("Agent", StringComparison.OrdinalIgnoreCase) && !RootFiles.Contains(root))
                throw LayoutError();
            if (!root.Equals("Agent", StringComparison.OrdinalIgnoreCase) && (canonical.Contains('/') || directory))
                throw LayoutError();
            if (canonical.Equals("Agent", StringComparison.OrdinalIgnoreCase) && !directory)
                throw LayoutError();
            if (canonical.Equals("Agent/appsettings.json", StringComparison.OrdinalIgnoreCase)
                || Regex.IsMatch(parts[^1], @"\Atightvnc.*\.msi\z", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
                throw LayoutError();
            if (directory && (entry.Method != 0 || entry.Length != 0 || entry.CompressedLength != 0 || entry.Crc != 0)) throw StructureError();
        }
        foreach (var required in Required)
            if (!nodes.TryGetValue(required, out var node) || node.Directory || node.Entry is null) throw LayoutError();

        var manifest = nodes["AgentPackage.manifest.json"].Entry!;
        var deps = nodes["Agent/Agent.deps.json"].Entry!;
        if (manifest.Length is < 1 or > 65536) throw ManifestError();
        if (deps.Length is < 1 or > 16777216) throw DepsError();

        var ordered = nodes.Values.OrderBy(n => n.Path, StringComparer.Ordinal).ToArray();
        var inventoryNodes = new List<PackageNode>(ordered.Length);
        var tempStreams = new List<Stream>();
        Stream? manifestData = null, depsData = null, dllData = null, exeData = null;
        long total = 0;
        try
        {
            foreach (var node in ordered)
            {
                if (node.Directory)
                {
                    inventoryNodes.Add(new PackageNode(node.Path, true, 0, null));
                    continue;
                }
                var entry = node.Entry!;
                total += entry.Length;
                if (total > MaxExpanded) throw LimitError();
                Stream? capture = null;
                if (ReferenceEquals(entry, manifest) || ReferenceEquals(entry, deps)) capture = new MemoryStream();
                else if (node.Path.Equals("Agent/Agent.dll", StringComparison.OrdinalIgnoreCase)
                    || node.Path.Equals("Agent/Agent.exe", StringComparison.OrdinalIgnoreCase))
                    capture = TemporaryFile();
                if (capture is not null) tempStreams.Add(capture);
                var hash = CopyEntry(bytes, entry, capture);
                if (capture is not null) capture.Position = 0;
                if (ReferenceEquals(entry, manifest)) manifestData = capture;
                if (ReferenceEquals(entry, deps)) depsData = capture;
                if (node.Path.Equals("Agent/Agent.dll", StringComparison.OrdinalIgnoreCase)) dllData = capture;
                if (node.Path.Equals("Agent/Agent.exe", StringComparison.OrdinalIgnoreCase)) exeData = capture;
                inventoryNodes.Add(new PackageNode(node.Path, false, entry.Length, hash));
            }
            ValidateManifest(manifestData!, version);
            ValidateDeps(depsData!, version);
            ValidateAssembly(dllData!, version);
            var runtime = ReadRuntime(exeData!);
            var archiveHash = Convert.ToHexStringLower(SHA256.HashData(bytes));
            return new Analysis(new PackageInventory(archiveHash, version, runtime, inventoryNodes, total, InventoryDigest(inventoryNodes)), ordered);
        }
        finally { foreach (var stream in tempStreams) stream.Dispose(); }
    }

    private static string ValidatePath(string name, bool directory)
    {
        if (directory) name = name[..^1];
        if (name.Length is < 1 or > 240 || name.Contains('\\') || name.StartsWith('/')) throw PathError();
        var segments = name.Split('/');
        if (segments.Length > 16) throw PathError();
        foreach (var segment in segments)
        {
            if (segment.Length is < 1 or > 100 || segment is "." or ".." || segment.EndsWith('.') || segment.EndsWith(' ')
                || DevicePattern.IsMatch(segment.Split('.')[0])) throw PathError();
            if (segment.Any(c => c < 32 || c > 126 || c is '<' or '>' or ':' or '"' or '|' or '?' or '*')) throw PathError();
        }
        return name;
    }

    private static void ValidateAttributes(ZipStructureReader.Entry entry, bool directory)
    {
        var unixType = (entry.Attributes >> 16) & 0xf000;
        var dos = entry.Attributes & 0xffff;
        if (unixType is not (0 or 0x8000 or 0x4000)
            || unixType == 0x8000 && directory || unixType == 0x4000 && !directory
            || (dos & 0x10) != 0 && !directory || (dos & (0x08 | 0x400)) != 0) throw StructureError();
    }

    private static string CopyEntry(byte[] bytes, ZipStructureReader.Entry entry, Stream? output)
    {
        try
        {
            if (entry.Method == 8)
                DeflateInspector.Verify(bytes.AsSpan(entry.DataOffset, entry.CompressedLength), entry.Length);
            using var raw = new MemoryStream(bytes, entry.DataOffset, entry.CompressedLength, false);
            using Stream input = entry.Method == 0 ? raw : new DeflateStream(raw, CompressionMode.Decompress, leaveOpen: true);
            if (entry.Method == 0 && entry.CompressedLength != entry.Length) throw StructureError();
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var buffer = new byte[65536];
            long length = 0;
            uint crc = uint.MaxValue;
            while (true)
            {
                var read = input.Read(buffer, 0, buffer.Length);
                if (read == 0) break;
                length += read;
                if (length > entry.Length || length > MaxExpanded) throw StructureError();
                for (var i = 0; i < read; i++) crc = CrcTable[(crc ^ buffer[i]) & 0xff] ^ (crc >> 8);
                hash.AppendData(buffer, 0, read);
                output?.Write(buffer, 0, read);
            }
            if (length != entry.Length || ~crc != entry.Crc) throw StructureError();
            return Convert.ToHexStringLower(hash.GetHashAndReset());
        }
        catch (PackageValidationException) { throw; }
        catch (Exception e) when (e is InvalidDataException or IOException or ArgumentException)
        { throw StructureError(); }
    }

    private static uint[] BuildCrcTable()
    {
        var table = new uint[256];
        for (uint i = 0; i < 256; i++)
        {
            var value = i;
            for (var b = 0; b < 8; b++) value = (value & 1) != 0 ? 0xedb88320 ^ (value >> 1) : value >> 1;
            table[i] = value;
        }
        return table;
    }

    private static Stream TemporaryFile()
    {
        var path = Path.Combine(Path.GetTempPath(), "kr-agent-metadata-" + Guid.NewGuid().ToString("N"));
        return new FileStream(path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 65536, FileOptions.DeleteOnClose | FileOptions.SequentialScan);
    }

    private static string InventoryDigest(IEnumerable<PackageNode> nodes)
    {
        using var memory = new MemoryStream();
        using (var writer = new BinaryWriter(memory, Encoding.UTF8, true))
            foreach (var node in nodes)
            {
                writer.Write(node.Path);
                writer.Write(node.IsDirectory);
                writer.Write(node.Length);
                writer.Write(node.Sha256 ?? "");
            }
        return Convert.ToHexStringLower(SHA256.HashData(memory.ToArray()));
    }

    private static void ValidateManifest(Stream data, string version)
    {
        try
        {
            ValidateUtf8(data);
            using var json = JsonDocument.Parse(data, new JsonDocumentOptions { MaxDepth = 8 });
            var obj = json.RootElement;
            if (obj.ValueKind != JsonValueKind.Object) throw ManifestError();
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var expected = new HashSet<string>(["packageFormatVersion", "agentInformationalVersion", "sourceCommit", "sourceDirty", "releaseBuild", "agentUpdateChallengeProtocol"], StringComparer.Ordinal);
            foreach (var p in obj.EnumerateObject()) if (!names.Add(p.Name) || !expected.Remove(p.Name)) throw ManifestError();
            if (expected.Count != 0 || !Int(obj, "packageFormatVersion", 1) || !Int(obj, "agentUpdateChallengeProtocol", 2)
                || !String(obj, "agentInformationalVersion", version)
                || !obj.TryGetProperty("sourceCommit", out var commit) || commit.ValueKind != JsonValueKind.String
                || !CommitPattern.IsMatch(commit.GetString() ?? "")
                || obj.GetProperty("sourceDirty").ValueKind != JsonValueKind.False
                || obj.GetProperty("releaseBuild").ValueKind != JsonValueKind.True) throw ManifestError();
        }
        catch (PackageValidationException) { throw; }
        catch (Exception e) when (e is JsonException or InvalidOperationException or FormatException or DecoderFallbackException) { throw ManifestError(); }
    }

    private static void ValidateDeps(Stream data, string version)
    {
        try
        {
            ValidateUtf8(data);
            using var json = JsonDocument.Parse(data, new JsonDocumentOptions { MaxDepth = 32 });
            var obj = json.RootElement;
            if (obj.ValueKind != JsonValueKind.Object) throw DepsError();
            NoDuplicateProperties(obj);
            if (!obj.TryGetProperty("libraries", out var libraries) || libraries.ValueKind != JsonValueKind.Object) throw DepsError();
            var count = 0;
            foreach (var property in libraries.EnumerateObject())
            {
                var slash = property.Name.IndexOf('/');
                var project = slash < 0 ? property.Name : property.Name[..slash];
                if (!project.Equals("Agent", StringComparison.OrdinalIgnoreCase)) continue;
                count++;
                if (property.Name != "Agent/" + version || property.Value.ValueKind != JsonValueKind.Object
                    || !String(property.Value, "type", "project")) throw DepsError();
            }
            if (count != 1) throw DepsError();
        }
        catch (PackageValidationException) { throw; }
        catch (Exception e) when (e is JsonException or InvalidOperationException or DecoderFallbackException) { throw DepsError(); }
    }

    private static void ValidateUtf8(Stream stream)
    {
        var memory = (MemoryStream)stream;
        if (!memory.TryGetBuffer(out var bytes)) throw new InvalidOperationException();
        var data = bytes.AsSpan(0, checked((int)memory.Length));
        if (data.Length >= 3 && data[0] == 0xef && data[1] == 0xbb && data[2] == 0xbf) throw new DecoderFallbackException();
        StrictUtf8.GetCharCount(data);
    }

    private static void NoDuplicateProperties(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var property in element.EnumerateObject())
            {
                if (!names.Add(property.Name)) throw DepsError();
                NoDuplicateProperties(property.Value);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
            foreach (var value in element.EnumerateArray()) NoDuplicateProperties(value);
    }

    private static bool Int(JsonElement obj, string name, int expected)
        => obj.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var actual) && actual == expected;
    private static bool String(JsonElement obj, string name, string expected)
        => obj.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String && value.GetString() == expected;

    private static void ValidateAssembly(Stream data, string version)
    {
        try
        {
            using var pe = new PEReader(data, PEStreamOptions.LeaveOpen);
            if (!pe.HasMetadata) throw MetadataError();
            var reader = pe.GetMetadataReader();
            if (!reader.IsAssembly) throw MetadataError();
            var versionCount = 0;
            var protocolCount = 0;
            foreach (var handle in reader.GetAssemblyDefinition().GetCustomAttributes())
            {
                var attribute = reader.GetCustomAttribute(handle);
                var isVersion = IsAttribute(reader, attribute.Constructor, "AssemblyInformationalVersionAttribute");
                var isMetadata = IsAttribute(reader, attribute.Constructor, "AssemblyMetadataAttribute");
                if (!isVersion && !isMetadata) continue;
                var blob = reader.GetBlobReader(attribute.Value);
                if (blob.ReadUInt16() != 1) throw MetadataError();
                var first = blob.ReadSerializedString();
                var second = isMetadata ? blob.ReadSerializedString() : null;
                if (blob.ReadUInt16() != 0 || blob.RemainingBytes != 0) throw MetadataError();
                if (isVersion && (++versionCount != 1 || first != version)) throw MetadataError();
                if (isMetadata && first == "AgentUpdateChallengeProtocol" && (++protocolCount != 1 || second != "2")) throw MetadataError();
            }
            if (versionCount != 1 || protocolCount != 1) throw MetadataError();
        }
        catch (PackageValidationException) { throw; }
        catch (Exception e) when (e is BadImageFormatException or IOException or ArgumentOutOfRangeException or InvalidOperationException)
        { throw MetadataError(); }
    }

    private static bool IsAttribute(MetadataReader reader, EntityHandle constructor, string name)
    {
        var type = constructor.Kind switch
        {
            HandleKind.MemberReference => reader.GetMemberReference((MemberReferenceHandle)constructor).Parent,
            HandleKind.MethodDefinition => reader.GetMethodDefinition((MethodDefinitionHandle)constructor).GetDeclaringType(),
            _ => default
        };
        if (type.Kind != HandleKind.TypeReference) return false;
        var reference = reader.GetTypeReference((TypeReferenceHandle)type);
        if (reader.GetString(reference.Namespace) != "System.Reflection" || reader.GetString(reference.Name) != name
            || reference.ResolutionScope.Kind != HandleKind.AssemblyReference) return false;
        var assembly = reader.GetString(reader.GetAssemblyReference((AssemblyReferenceHandle)reference.ResolutionScope).Name);
        return assembly is "System.Runtime" or "System.Private.CoreLib" or "mscorlib";
    }

    private static string ReadRuntime(Stream data)
    {
        try
        {
            using var pe = new PEReader(data, PEStreamOptions.LeaveOpen);
            var headers = pe.PEHeaders;
            if (headers.PEHeader is null || headers.PEHeader.Magic != PEMagic.PE32Plus
                || (headers.CoffHeader.Characteristics & Characteristics.ExecutableImage) == 0
                || (headers.CoffHeader.Characteristics & Characteristics.Dll) != 0 || pe.HasMetadata) throw RuntimeError();
            return headers.CoffHeader.Machine switch { Machine.Amd64 => "win-x64", Machine.Arm64 => "win-arm64", _ => throw RuntimeError() };
        }
        catch (PackageValidationException) { throw; }
        catch (Exception e) when (e is BadImageFormatException or IOException or InvalidOperationException or ArgumentOutOfRangeException)
        { throw RuntimeError(); }
    }

    private static void MatchInventory(PackageInventory actual, PackageInventory expected)
    {
        if (actual.ArchiveHash != expected.ArchiveHash || actual.TargetRuntime != expected.TargetRuntime
            || actual.InventoryDigest != expected.InventoryDigest || actual.TotalBytes != expected.TotalBytes
            || !actual.Nodes.SequenceEqual(expected.Nodes)) throw InventoryError();
    }

    private static string CheckRoot(string root, PackageInventory expected)
    {
        try
        {
            if (!OperatingSystem.IsWindows() || !Path.IsPathFullyQualified(root)) throw InventoryError();
            root = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (!Directory.Exists(root) || root.Length + 1 + expected.Nodes.Max(n => n.Path.Length) > 247) throw InventoryError();
            return root;
        }
        catch (PackageValidationException) { throw; }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException) { throw InventoryError(); }
    }

    private static string LocalPath(string root, string relative) => Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));

    private static SafeFileHandle OpenNode(string path, bool directory)
    {
        var handle = CreateFileW(path, 0x80000000, 1, IntPtr.Zero, 3, 0x00200000 | (directory ? 0x02000000u : 0), IntPtr.Zero);
        if (handle.IsInvalid) { handle.Dispose(); throw InventoryError(); }
        if (!GetFileInformationByHandle(handle, out var info) || (info.Attributes & 0x400) != 0
            || ((info.Attributes & 0x10) != 0) != directory || (!directory && info.Links != 1))
        { handle.Dispose(); throw InventoryError(); }
        return handle;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ByHandleFileInformation
    {
        internal uint Attributes;
        internal System.Runtime.InteropServices.ComTypes.FILETIME Creation, Access, Write;
        internal uint Volume, SizeHigh, SizeLow, Links, IndexHigh, IndexLow;
    }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(string name, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandle(SafeFileHandle handle, out ByHandleFileInformation information);

    private static PackageValidationException StructureError() => new("structure", "Package ZIP structure or contents are invalid.");
    private static PackageValidationException LimitError() => new("limit", "Package exceeds a resource limit.");
    private static PackageValidationException PathError() => new("path", "Package path is invalid or ambiguous.");
    private static PackageValidationException LayoutError() => new("layout", "Package layout is invalid.");
    private static PackageValidationException ManifestError() => new("manifest", "Package manifest is invalid.");
    private static PackageValidationException DepsError() => new("deps", "Agent dependency manifest is invalid.");
    private static PackageValidationException MetadataError() => new("metadata", "Agent assembly metadata is invalid.");
    private static PackageValidationException RuntimeError() => new("runtime", "Agent executable machine type is invalid.");
    private static PackageValidationException InventoryError() => new("inventory", "Extracted tree does not match the package inventory.");
}

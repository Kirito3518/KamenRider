using System.IO.Compression;
using System.Text.Json;
using Controller.Tests.Fixtures;

namespace Controller.Tests;

internal static class AgentUpdateTestPackage
{
    internal static byte[] Create(string version, bool legacyAssembly = false, bool legacyManifest = false)
    {
        var assembly = legacyAssembly ? typeof(LegacyAgentMetadataFixture).Assembly : version switch
        {
            "2.0.0" => typeof(AgentVersionTwo).Assembly,
            "3.0.0" => typeof(AgentVersionThree).Assembly,
            _ => typeof(AgentMetadataFixture).Assembly,
        };
        using var output = new MemoryStream();
        using (var zip = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
        {
            using (var binary = zip.CreateEntry("Agent/Agent.dll").Open())
            using (var input = File.OpenRead(assembly.Location)) input.CopyTo(binary);
            Add("Agent/Agent.exe", "inert test executable");
            Add("Agent/Agent.deps.json", JsonSerializer.Serialize(new { libraries = new Dictionary<string, object> { ["Agent/" + version] = new { type = "project" } } }));
            foreach (var script in new[] { "Install-AgentPackage", "Install-Service", "Install-TightVncServer", "Prepare-SunshineStreamGate", "PrivateInstallFiles", "ServiceInstallTransaction", "Update-AgentPackage", "Update-Agent" })
                Add(script + ".ps1", "# inert test script");
            var manifest = new Dictionary<string, object> { ["packageFormatVersion"] = 1, ["agentInformationalVersion"] = version,
                ["sourceCommit"] = "0123456789ab", ["sourceDirty"] = false, ["releaseBuild"] = true };
            if (!legacyManifest) manifest["agentUpdateChallengeProtocol"] = 1;
            Add("AgentPackage.manifest.json", JsonSerializer.Serialize(manifest));
            void Add(string name, string value) { using var writer = new StreamWriter(zip.CreateEntry(name).Open()); writer.Write(value); }
        }
        return output.ToArray();
    }
}

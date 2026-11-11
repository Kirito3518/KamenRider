using System.IO.Compression;
using System.Text.Json;
using Controller.Tests.Fixtures;

namespace Controller.Tests;

internal static class AgentUpdateTestPackage
{
    internal static byte[] Create(string version, bool legacyAssembly = false, bool legacyManifest = false, int challengeProtocol = 2)
    {
        if (challengeProtocol == 1) legacyAssembly = true;
        var assembly = legacyAssembly ? typeof(LegacyAgentMetadataFixture).Assembly : version switch
        {
            "2.0.0" => typeof(AgentVersionTwo).Assembly,
            "3.0.0" => typeof(AgentVersionThree).Assembly,
            _ => typeof(AgentMetadataFixture).Assembly,
        };
        var entries = AgentPackageCorpus.Entries().Select(entry => (entry.Name, entry.Bytes.ToArray())).ToList();
        entries[entries.FindIndex(entry => entry.Name == "Agent/Agent.dll")] = ("Agent/Agent.dll", File.ReadAllBytes(assembly.Location));
        entries[entries.FindIndex(entry => entry.Name == "Agent/Agent.deps.json")] = ("Agent/Agent.deps.json",
            AgentPackageCorpus.Utf8(JsonSerializer.Serialize(new { libraries = new Dictionary<string, object> { ["Agent/" + version] = new { type = "project" } } })));
        var manifest = new Dictionary<string, object> { ["packageFormatVersion"] = 1, ["agentInformationalVersion"] = version,
            ["sourceCommit"] = "0123456789ab", ["sourceDirty"] = false, ["releaseBuild"] = true };
        if (!legacyManifest) manifest["agentUpdateChallengeProtocol"] = challengeProtocol;
        entries[entries.FindIndex(entry => entry.Name == "AgentPackage.manifest.json")] =
            ("AgentPackage.manifest.json", AgentPackageCorpus.Utf8(JsonSerializer.Serialize(manifest)));
        return AgentPackageCorpus.Build(entries);
    }
}

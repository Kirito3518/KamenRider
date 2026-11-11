using System.Text.Json;
using KamenRaider.AgentPackage;

if (args.Length != 7 || args[0] != "validate-archive")
    return 2;

var options = new Dictionary<string, string>(StringComparer.Ordinal);
for (var i = 1; i < args.Length; i += 2)
{
    if (args[i] is not ("--archive" or "--version" or "--output-json") || !options.TryAdd(args[i], args[i + 1]))
        return 2;
}
if (options.Count != 3) return 2;

string? category = null;
PackageInventory? inventory = null;
try
{
    using var input = new FileStream(options["--archive"], FileMode.Open, FileAccess.Read, FileShare.Read);
    inventory = PackageVerifier.ValidateArchive(input, options["--version"]);
}
catch (PackageValidationException e) { category = e.Category; }
catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
{ category = "io"; }

try
{
    var result = new VerificationResult(category is null ? "accepted" : "rejected", category,
        inventory?.TargetRuntime, inventory?.Version, inventory?.AgentUpdateChallengeProtocol, inventory?.ArchiveHash);
    var json = JsonSerializer.SerializeToUtf8Bytes(result, new JsonSerializerOptions(JsonSerializerDefaults.Web));
    using var output = new FileStream(options["--output-json"], FileMode.CreateNew, FileAccess.Write, FileShare.None);
    output.Write(json);
    return category is null ? 0 : 1;
}
catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
{ return 2; }

internal sealed record VerificationResult(string Status, string? Category, string? TargetRuntime, string? Version,
    int? AgentUpdateChallengeProtocol, string? ArchiveHash);

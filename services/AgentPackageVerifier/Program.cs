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
string? runtime = null;
try
{
    using var input = new FileStream(options["--archive"], FileMode.Open, FileAccess.Read, FileShare.Read);
    runtime = PackageVerifier.ValidateArchive(input, options["--version"]).TargetRuntime;
}
catch (PackageValidationException e) { category = e.Category; }
catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
{ category = "io"; }

try
{
    var json = JsonSerializer.SerializeToUtf8Bytes(new { status = category is null ? "accepted" : "rejected", category, runtime });
    using var output = new FileStream(options["--output-json"], FileMode.CreateNew, FileAccess.Write, FileShare.None);
    output.Write(json);
    return category is null ? 0 : 1;
}
catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
{ return 2; }

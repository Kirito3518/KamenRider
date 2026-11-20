using System.Text.Json.Serialization;
using KamenRaider.Contracts;

namespace KamenRaider.Agent.Parsec;

/// <summary>
/// Source-generated JSON metadata for the Parsec flow.
/// </summary>
/// <remarks>
/// The translation table, the desktop script status and the signed status report are the only
/// documents this subsystem parses or writes. Source generation keeps them working under trimming
/// or AOT, where reflection-based serialization is disabled, and keeps the embedded-resource
/// contract explicit.
/// </remarks>
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(ParsecTranslationDocument))]
[JsonSerializable(typeof(ParsecScriptStatus))]
[JsonSerializable(typeof(ParsecStatusReport))]
internal sealed partial class ParsecJsonContext : JsonSerializerContext;

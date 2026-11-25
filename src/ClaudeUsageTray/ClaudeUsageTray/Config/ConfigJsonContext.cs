using System.Text.Json.Serialization;
using ClaudeUsageTray.Config.Models;

namespace ClaudeUsageTray.Config;

/// <summary>Source-generated serializer for the config: no reflection-based metadata at startup.</summary>
[JsonSourceGenerationOptions(WriteIndented = true, UseStringEnumConverter = true)]
[JsonSerializable(typeof(ConfigData))]
internal sealed partial class ConfigJsonContext : JsonSerializerContext
{
}

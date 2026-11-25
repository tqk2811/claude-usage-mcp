using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using ClaudeUsageTray.Usage;
using ClaudeUsageTray.Usage.Models;

namespace ClaudeUsageTray.Mcp;

/// <summary>
/// Transport-independent MCP core equivalent to <c>src/node/index.js</c>: answers one JSON-RPC message, tool
/// <c>get_usage</c> (<c>raw</c>, <c>force</c>), same text output. Thread-safe; does not own the provider.
/// </summary>
public sealed class McpRequestHandler
{
    private const string DefaultProtocolVersion = "2025-06-18";

    /// <summary>Like JSON.stringify: non-ASCII kept as is. Transports serialize responses with it.</summary>
    public static readonly JsonSerializerOptions CompactJson = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
    private static readonly JsonSerializerOptions _indented = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping, WriteIndented = true };

    private readonly UsageProvider _usage;

    public McpRequestHandler(UsageProvider usage)
    {
        _usage = usage;
    }

    /// <summary>The response to a body that is not valid JSON.</summary>
    public static JsonObject ParseError()
        => new() { ["jsonrpc"] = "2.0", ["id"] = null, ["error"] = new JsonObject { ["code"] = -32700, ["message"] = "Parse error" } };

    /// <summary>Handles one parsed message.</summary>
    /// <returns>The response; <c>null</c> for notifications and client responses (nothing to send).</returns>
    public async Task<JsonObject?> HandleAsync(JsonNode? msg)
    {
        JsonObject? obj = msg as JsonObject;
        bool hasId = obj?.ContainsKey("id") == true;
        try
        {
            JsonNode? result = await HandleAsync(obj, hasId);
            if (hasId && result is not null)
                return new JsonObject { ["jsonrpc"] = "2.0", ["id"] = obj!["id"]?.DeepClone(), ["result"] = result };
            return null;
        }
        catch (Exception ex)
        {
            if (!hasId)
                return null;
            int code = ex is JsonRpcException rpc ? rpc.Code : -32603;
            return new JsonObject
            {
                ["jsonrpc"] = "2.0",
                ["id"] = obj!["id"]?.DeepClone(),
                ["error"] = new JsonObject { ["code"] = code, ["message"] = ex.Message },
            };
        }
    }

    /// <returns><c>null</c> for notifications (nothing is sent).</returns>
    private async Task<JsonNode?> HandleAsync(JsonObject? msg, bool hasId)
    {
        string? method = msg?["method"] is JsonValue m && m.GetValueKind() == JsonValueKind.String ? m.GetValue<string>() : null;
        JsonNode? parameters = msg?["params"];
        switch (method)
        {
            case "initialize":
                JsonNode? requested = parameters?["protocolVersion"];
                bool truthy = requested is JsonValue v && v.GetValueKind() == JsonValueKind.String && v.GetValue<string>().Length > 0;
                return new JsonObject
                {
                    ["protocolVersion"] = truthy ? requested!.DeepClone() : DefaultProtocolVersion,
                    ["capabilities"] = new JsonObject { ["tools"] = new JsonObject() },
                    ["serverInfo"] = new JsonObject { ["name"] = "claude-usage", ["version"] = UsageApiClient.ClientVersion },
                };
            case "ping":
                return new JsonObject();
            case "tools/list":
                return new JsonObject { ["tools"] = new JsonArray(BuildTool()) };
            case "tools/call":
                string? name = parameters?["name"] is JsonValue n && n.GetValueKind() == JsonValueKind.String ? n.GetValue<string>() : null;
                return await CallToolAsync(name, parameters?["arguments"] as JsonObject);
            default:
                if (!hasId)
                    return null;
                throw new JsonRpcException(-32601, $"Method not found: {method}");
        }
    }

    private async Task<JsonNode> CallToolAsync(string? name, JsonObject? args)
    {
        if (name != "get_usage")
            throw new JsonRpcException(-32602, $"Unknown tool: {name ?? "undefined"}");
        try
        {
            UsageResult usage = await _usage.GetAsync(IsTrue(args?["force"]));
            List<UsageWindow> windows = UsageFormatter.Summarize(usage.Data);
            JsonObject summary = UsageFormatter.ToSummaryJson(windows);
            JsonObject payload = IsTrue(args?["raw"])
                ? new JsonObject { ["summary"] = summary.DeepClone(), ["raw"] = usage.Data.DeepClone() }
                : (JsonObject)summary.DeepClone();
            // .NET 8 indents with Environment.NewLine; JSON.stringify uses "\n" (string values never hold a raw CR).
            string json = payload.ToJsonString(_indented).Replace("\r\n", "\n");
            string text = UsageFormatter.ToText(windows, usage.Data) + "\n\n" + json;

            return new JsonObject
            {
                ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = text }),
                ["structuredContent"] = payload,
            };
        }
        catch (Exception ex)
        {
            return new JsonObject
            {
                ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = $"Error: {ex.Message}" }),
                ["isError"] = true,
            };
        }
    }

    private static bool IsTrue(JsonNode? node) => node is JsonValue v && v.GetValueKind() == JsonValueKind.True;

    private static JsonObject BuildTool() => new()
    {
        ["name"] = "get_usage",
        ["description"] = "Get current Claude subscription usage: 5-hour session and weekly limits (percent used and reset time).",
        ["inputSchema"] = new JsonObject
        {
            ["type"] = "object",
            ["properties"] = new JsonObject
            {
                ["raw"] = new JsonObject { ["type"] = "boolean", ["description"] = "Also return the raw API response." },
                ["force"] = new JsonObject { ["type"] = "boolean", ["description"] = "Bypass the 30s cache." },
            },
        },
    };
}

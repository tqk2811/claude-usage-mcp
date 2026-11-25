using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using ClaudeUsageTray.Usage;
using ClaudeUsageTray.Usage.Models;

namespace ClaudeUsageTray.Mcp;

/// <summary>
/// Stdio MCP server equivalent to <c>src/node/index.js</c>: newline-delimited JSON-RPC, tool <c>get_usage</c>
/// (<c>raw</c>, <c>force</c>), same text output. Lines are handled concurrently like the Node version; writes are
/// serialized. Does not own the reader/writer/provider.
/// </summary>
public sealed class McpServer
{
    private const string DefaultProtocolVersion = "2025-06-18";

    /// <summary>Like JSON.stringify: non-ASCII kept as is.</summary>
    private static readonly JsonSerializerOptions _compact = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
    private static readonly JsonSerializerOptions _indented = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping, WriteIndented = true };

    private readonly UsageProvider _usage;
    private readonly TextReader _input;
    private readonly TextWriter _output;
    private readonly object _writeLock = new();

    public McpServer(UsageProvider usage, TextReader input, TextWriter output)
    {
        _usage = usage;
        _input = input;
        _output = output;
    }

    /// <summary>Runs until stdin closes, then waits for the requests still in progress.</summary>
    public async Task RunAsync()
    {
        List<Task> pending = [];
        while (await _input.ReadLineAsync() is { } line)
        {
            if (string.IsNullOrWhiteSpace(line))
                continue;
            pending.RemoveAll(x => x.IsCompleted);
            pending.Add(Task.Run(() => HandleLineAsync(line)));
        }
        await Task.WhenAll(pending);
    }

    private async Task HandleLineAsync(string line)
    {
        JsonNode? msg;
        try
        {
            msg = JsonNode.Parse(line);
        }
        catch (JsonException)
        {
            Send(new JsonObject { ["jsonrpc"] = "2.0", ["id"] = null, ["error"] = new JsonObject { ["code"] = -32700, ["message"] = "Parse error" } });
            return;
        }

        JsonObject? obj = msg as JsonObject;
        bool hasId = obj?.ContainsKey("id") == true;
        try
        {
            JsonNode? result = await HandleAsync(obj, hasId);
            if (hasId && result is not null)
                Send(new JsonObject { ["jsonrpc"] = "2.0", ["id"] = obj!["id"]?.DeepClone(), ["result"] = result });
        }
        catch (Exception ex)
        {
            if (hasId)
            {
                int code = ex is JsonRpcException rpc ? rpc.Code : -32603;
                Send(new JsonObject
                {
                    ["jsonrpc"] = "2.0",
                    ["id"] = obj!["id"]?.DeepClone(),
                    ["error"] = new JsonObject { ["code"] = code, ["message"] = ex.Message },
                });
            }
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

    private void Send(JsonObject message)
    {
        string json = message.ToJsonString(_compact);
        lock (_writeLock)
        {
            _output.Write(json);
            _output.Write('\n');
            _output.Flush();
        }
    }
}

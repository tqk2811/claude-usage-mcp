namespace ClaudeUsageTray.Mcp;

/// <summary>An error answered as a JSON-RPC <c>error</c> object with this code.</summary>
public sealed class JsonRpcException : Exception
{
    public JsonRpcException(int code, string message)
        : base(message)
    {
        Code = code;
    }

    public int Code { get; }
}

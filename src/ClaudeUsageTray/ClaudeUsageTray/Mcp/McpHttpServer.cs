using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using ClaudeUsageTray.Diagnostics;

namespace ClaudeUsageTray.Mcp;

/// <summary>
/// Minimal MCP Streamable HTTP transport on <c>http://127.0.0.1:&lt;port&gt;/mcp</c> through <see cref="HttpListener"/>
/// (no ASP.NET Core: the tray has to stay light). POST carries one JSON-RPC message and is answered with
/// <c>application/json</c> (no SSE, no session); notifications and client responses get 202. A loopback prefix needs
/// no admin rights or urlacl. An <c>Origin</c> header that is not localhost is refused (DNS rebinding).
/// A failed listen (port in use...) is logged and kept in <see cref="LastError"/>, never thrown.
/// Owns the listener, not the handler.
/// </summary>
public sealed class McpHttpServer : IDisposable
{
    public const string Path = "/mcp";

    /// <summary>Larger bodies are refused with 413: a get_usage call is a few hundred bytes.</summary>
    private const int MaxBodyBytes = 1024 * 1024;

    private static readonly string[] _allowedOriginHosts = ["localhost", "127.0.0.1", "[::1]", "::1"];

    private readonly McpRequestHandler _handler;
    private readonly FileLogger _log;
    private HttpListener? _listener;

    public McpHttpServer(McpRequestHandler handler, FileLogger log)
    {
        _handler = handler;
        _log = log;
    }

    /// <summary>Raised on the thread that called <see cref="Start"/> or <see cref="Stop"/>.</summary>
    public event EventHandler? StateChanged;

    /// <summary>Port of the last <see cref="Start"/>, whether it succeeded or not; <c>null</c> when stopped.</summary>
    public int? Port { get; private set; }

    public bool IsListening => _listener is not null;

    /// <summary>Why the last <see cref="Start"/> failed; <c>null</c> while listening or stopped.</summary>
    public Exception? LastError { get; private set; }

    public static string BuildUrl(int port) => $"http://127.0.0.1:{port}{Path}";

    /// <summary>(Re)starts on <paramref name="port"/>; stops the current listener first.</summary>
    public void Start(int port)
    {
        StopListener();
        Port = port;
        LastError = null;
        HttpListener listener = new();
        try
        {
            listener.Prefixes.Add($"http://127.0.0.1:{port}/");
            listener.Start();
            _listener = listener;
            _log.Info($"MCP HTTP server listening on {BuildUrl(port)}");
            _ = AcceptLoopAsync(listener);
        }
        catch (Exception ex)
        {
            LastError = ex;
            _log.Error($"MCP HTTP server could not listen on port {port}", ex);
            listener.Close();
        }
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    public void Stop()
    {
        StopListener();
        Port = null;
        LastError = null;
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    public void Dispose() => StopListener();

    private void StopListener()
    {
        HttpListener? listener = _listener;
        _listener = null;
        if (listener is null)
            return;
        listener.Close();
        _log.Info("MCP HTTP server stopped");
    }

    private async Task AcceptLoopAsync(HttpListener listener)
    {
        while (true)
        {
            HttpListenerContext context;
            try
            {
                context = await listener.GetContextAsync();
            }
            catch (Exception ex) when (ex is HttpListenerException or ObjectDisposedException or InvalidOperationException)
            {
                // Closed by Stop/Start, or the listener died: either way this loop is done.
                if (ReferenceEquals(_listener, listener))
                    _log.Error("MCP HTTP accept loop ended", ex);
                return;
            }
            _ = Task.Run(() => ProcessAsync(context));
        }
    }

    private async Task ProcessAsync(HttpListenerContext context)
    {
        HttpListenerResponse response = context.Response;
        try
        {
            HttpListenerRequest request = context.Request;
            if (!string.Equals(request.Url?.AbsolutePath.TrimEnd('/'), Path, StringComparison.OrdinalIgnoreCase))
            {
                Finish(response, HttpStatusCode.NotFound);
                return;
            }
            if (!IsAllowedOrigin(request.Headers["Origin"]))
            {
                Finish(response, HttpStatusCode.Forbidden);
                return;
            }
            if (request.HttpMethod != "POST")
            {
                response.AddHeader("Allow", "POST");
                Finish(response, HttpStatusCode.MethodNotAllowed);
                return;
            }
            if (request.ContentLength64 > MaxBodyBytes)
            {
                Finish(response, HttpStatusCode.RequestEntityTooLarge);
                return;
            }

            byte[]? body = await ReadBodyAsync(request.InputStream);
            if (body is null)
            {
                Finish(response, HttpStatusCode.RequestEntityTooLarge);
                return;
            }
            JsonNode? message;
            try
            {
                message = JsonNode.Parse(body);
            }
            catch (JsonException)
            {
                await WriteJsonAsync(response, HttpStatusCode.BadRequest, McpRequestHandler.ParseError());
                return;
            }

            JsonObject? reply = await _handler.HandleAsync(message);
            if (reply is null)
                Finish(response, HttpStatusCode.Accepted);
            else
                await WriteJsonAsync(response, HttpStatusCode.OK, reply);
        }
        catch (Exception ex)
        {
            _log.Error("MCP HTTP request failed", ex);
            try
            {
                Finish(response, HttpStatusCode.InternalServerError);
            }
            catch (Exception)
            {
                // Client gone or response already sent.
            }
        }
    }

    /// <returns><c>null</c> when the body is larger than <see cref="MaxBodyBytes"/> (chunked bodies have no length).</returns>
    private static async Task<byte[]?> ReadBodyAsync(Stream input)
    {
        using MemoryStream buffer = new();
        byte[] chunk = new byte[8192];
        int read;
        while ((read = await input.ReadAsync(chunk)) > 0)
        {
            if (buffer.Length + read > MaxBodyBytes)
                return null;
            buffer.Write(chunk, 0, read);
        }
        return buffer.ToArray();
    }

    /// <summary>No header (CLI clients) is fine; a browser page must come from localhost.</summary>
    private static bool IsAllowedOrigin(string? origin)
    {
        if (origin is null)
            return true;
        return Uri.TryCreate(origin, UriKind.Absolute, out Uri? uri)
            && _allowedOriginHosts.Contains(uri.Host, StringComparer.OrdinalIgnoreCase);
    }

    private static async Task WriteJsonAsync(HttpListenerResponse response, HttpStatusCode status, JsonObject body)
    {
        byte[] bytes = new UTF8Encoding(false).GetBytes(body.ToJsonString(McpRequestHandler.CompactJson));
        response.StatusCode = (int)status;
        response.ContentType = "application/json; charset=utf-8";
        response.ContentLength64 = bytes.Length;
        await response.OutputStream.WriteAsync(bytes);
        response.Close();
    }

    private static void Finish(HttpListenerResponse response, HttpStatusCode status)
    {
        response.StatusCode = (int)status;
        response.ContentLength64 = 0;
        response.Close();
    }
}

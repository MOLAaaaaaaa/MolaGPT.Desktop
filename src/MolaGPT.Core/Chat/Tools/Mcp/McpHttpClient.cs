using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using MolaGPT.Core.Chat.LocalTools;
using MolaGPT.Core.Sse;

namespace MolaGPT.Core.Chat.Tools.Mcp;

public sealed class McpHttpClient
{
    private readonly HttpClient _http;
    private long _nextId;

    public McpHttpClient(HttpClient http) => _http = http;

    public async Task<McpSession> InitializeAsync(McpServerOptions server, CancellationToken ct)
    {
        var initialize = await SendAsync(server, null, "initialize", new
        {
            protocolVersion = "2025-03-26",
            capabilities = new { },
            clientInfo = new { name = "MolaGPT Desktop", version = "1.0" }
        }, ct).ConfigureAwait(false);

        var sessionId = initialize.SessionId;
        try
        {
            await SendNotificationAsync(server, sessionId, "notifications/initialized", ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Some lightweight servers accept initialize but not initialized.
        }

        return new McpSession(server, sessionId);
    }

    public async Task<IReadOnlyList<McpToolDescriptor>> ListToolsAsync(McpSession session, CancellationToken ct)
    {
        var result = await SendAsync(session.Server, session.SessionId, "tools/list", new { }, ct).ConfigureAwait(false);
        if (!result.Json.TryGetProperty("result", out var root)
            || !root.TryGetProperty("tools", out var tools)
            || tools.ValueKind != JsonValueKind.Array)
            return Array.Empty<McpToolDescriptor>();

        var list = new List<McpToolDescriptor>();
        foreach (var tool in tools.EnumerateArray())
        {
            var name = ReadString(tool, "name");
            if (string.IsNullOrWhiteSpace(name)) continue;
            list.Add(new McpToolDescriptor(
                name!,
                ReadString(tool, "description"),
                tool.TryGetProperty("inputSchema", out var schema) ? schema.Clone() : default));
        }

        return list;
    }

    public async Task<JsonElement> CallToolAsync(
        McpSession session,
        string toolName,
        JsonElement arguments,
        CancellationToken ct)
    {
        var result = await SendAsync(session.Server, session.SessionId, "tools/call", new
        {
            name = toolName,
            arguments
        }, ct).ConfigureAwait(false);

        return result.Json.Clone();
    }

    private async Task SendNotificationAsync(
        McpServerOptions server,
        string? sessionId,
        string method,
        CancellationToken ct)
    {
        using var req = CreateRequest(server, sessionId);
        req.Content = JsonContent.Create(new
        {
            jsonrpc = "2.0",
            method
        });
        using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();
    }

    private async Task<McpJsonRpcResponse> SendAsync(
        McpServerOptions server,
        string? sessionId,
        string method,
        object parameters,
        CancellationToken ct)
    {
        using var req = CreateRequest(server, sessionId);
        req.Content = JsonContent.Create(new
        {
            jsonrpc = "2.0",
            id = Interlocked.Increment(ref _nextId),
            method,
            @params = parameters
        });

        using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();
        var returnedSessionId = resp.Headers.TryGetValues("Mcp-Session-Id", out var values)
            ? values.FirstOrDefault()
            : sessionId;
        await using var stream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        var json = await ReadJsonResponseAsync(stream, resp.Content.Headers.ContentType?.MediaType, ct).ConfigureAwait(false);

        if (json.TryGetProperty("error", out var error) && error.ValueKind != JsonValueKind.Null)
            throw new InvalidOperationException(error.ToString());

        return new McpJsonRpcResponse(json.Clone(), returnedSessionId);
    }

    private HttpRequestMessage CreateRequest(McpServerOptions server, string? sessionId)
    {
        var req = new HttpRequestMessage(HttpMethod.Post, server.Url);
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
        if (!string.IsNullOrWhiteSpace(sessionId))
            req.Headers.TryAddWithoutValidation("Mcp-Session-Id", sessionId);
        if (!string.IsNullOrWhiteSpace(server.Token))
        {
            var header = string.IsNullOrWhiteSpace(server.HeaderName) ? "Authorization" : server.HeaderName.Trim();
            var value = server.Token!.Trim();
            if (string.Equals(header, "Authorization", StringComparison.OrdinalIgnoreCase)
                && !value.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
                value = "Bearer " + value;
            req.Headers.TryAddWithoutValidation(header, value);
        }
        return req;
    }

    private static async Task<JsonElement> ReadJsonResponseAsync(Stream stream, string? contentType, CancellationToken ct)
    {
        if (string.Equals(contentType, "text/event-stream", StringComparison.OrdinalIgnoreCase))
        {
            await foreach (var ev in SseStreamReader.ReadAsync(stream, ct).ConfigureAwait(false))
            {
                if (ev.IsDone || string.IsNullOrWhiteSpace(ev.Data)) continue;
                using var doc = JsonDocument.Parse(ev.Data);
                return doc.RootElement.Clone();
            }

            throw new InvalidOperationException("MCP server returned an empty SSE response.");
        }

        using var docJson = await JsonDocument.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false);
        return docJson.RootElement.Clone();
    }

    private static string? ReadString(JsonElement obj, string name) =>
        obj.ValueKind == JsonValueKind.Object
        && obj.TryGetProperty(name, out var value)
        && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private sealed record McpJsonRpcResponse(JsonElement Json, string? SessionId);
}

public sealed record McpSession(McpServerOptions Server, string? SessionId);

public sealed record McpToolDescriptor(string Name, string? Description, JsonElement InputSchema);

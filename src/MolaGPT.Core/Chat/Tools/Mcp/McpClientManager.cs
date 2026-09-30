using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using MolaGPT.Core.Chat.LocalTools;
using ModelContextProtocol;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace MolaGPT.Core.Chat.Tools.Mcp;

/// <summary>
/// Connections to the configured MCP servers, one per server for the whole app:
/// every conversation and every Pi sidecar shares it.
///
/// Pi ships its own MCP client, but it ties connections to a session and closes
/// them on <c>switch_session</c>, which the sidecar pool sends before every turn.
/// A stdio server would be restarted each turn, losing whatever state it held, and
/// each of up to eleven sidecars would start its own copy.
/// </summary>
public sealed class McpClientManager : IAsyncDisposable
{
    /// <summary>How long a failed connection is remembered before the next turn
    /// tries again. Short: a server that was still starting should not stay out
    /// for long, and a changed configuration reconnects at once regardless.</summary>
    private static readonly TimeSpan RetryAfter = TimeSpan.FromSeconds(15);

    private static readonly TimeSpan HttpConnectTimeout = TimeSpan.FromSeconds(30);

    /// <summary>Longer: the first start of an <c>npx</c> or <c>uvx</c> server
    /// installs it, measured at 39 s for <c>mcp-server-fetch</c>.</summary>
    private static readonly TimeSpan StdioConnectTimeout = TimeSpan.FromSeconds(180);

    /// <summary>How long a turn waits for a server's tools. A connection still
    /// opening after that carries on, and a later turn gets them.</summary>
    private static readonly TimeSpan TurnWait = TimeSpan.FromSeconds(30);

    private readonly HttpClient _http;
    private readonly Action<string>? _log;
    private readonly ConcurrentDictionary<string, Connection> _connections = new(StringComparer.Ordinal);

    /// <summary>Fingerprints of stdio servers that exit on the discover probe, so
    /// a reconnect does not start them twice.</summary>
    private readonly ConcurrentDictionary<string, byte> _initializeOnly = new(StringComparer.Ordinal);
    private readonly CancellationTokenSource _lifetime = new();

    public McpClientManager(HttpClient http, Action<string>? log = null)
    {
        _http = http;
        _log = log;
    }

    /// <summary>A server could not be reached while a turn gathered its tools. The
    /// turn goes ahead without them.</summary>
    public event Action<McpServerOptions, string>? ServerUnavailable;

    private sealed class Connection(string fingerprint)
    {
        public string Fingerprint { get; } = fingerprint;
        public object Gate { get; } = new();
        public Task<Session>? Client;
        public DateTimeOffset FailedAt;

        /// <summary>Cleared when the server announces that its tools changed.</summary>
        public Task<McpServerTools>? Tools;
    }

    /// <summary>The server's tools, or null when it cannot be reached right now.</summary>
    public async Task<McpServerTools?> TryGetToolsAsync(McpServerOptions server, CancellationToken ct)
    {
        var listing = GetToolsAsync(server, ct);
        try
        {
            return await listing.WaitAsync(TurnWait, ct).ConfigureAwait(false);
        }
        catch (TimeoutException) when (!listing.IsCompleted)
        {
            _ = listing.ContinueWith(
                static task => _ = task.Exception,
                CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
            Report(server, "尚未启动完成，本轮未使用其工具。");
            return null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            Report(server, Describe(ex));
            return null;
        }
    }

    private void Report(McpServerOptions server, string message)
    {
        _log?.Invoke($"[mcp] {server.Name}：{message}");
        ServerUnavailable?.Invoke(server, message);
    }

    /// <summary>The tools already listed for <paramref name="server"/>, or null.
    /// Never connects and never waits.</summary>
    public McpServerTools? ListedTools(McpServerOptions server)
    {
        if (!_connections.TryGetValue(server.Id, out var connection) || connection.Fingerprint != Fingerprint(server))
            return null;
        lock (connection.Gate)
            return connection.Tools is { IsCompletedSuccessfully: true } tools ? tools.Result : null;
    }

    public async Task<McpServerTools> GetToolsAsync(McpServerOptions server, CancellationToken ct)
    {
        var connection = ConnectionFor(server);
        Task<McpServerTools> tools;
        lock (connection.Gate)
        {
            tools = connection.Tools is { IsFaulted: false, IsCanceled: false } cached
                ? cached
                : connection.Tools = ListToolsAsync(server, connection);
        }
        return await tools.WaitAsync(ct).ConfigureAwait(false);
    }

    public async Task<McpToolDescriptor?> GetToolDescriptorAsync(
        McpServerOptions server,
        string toolSlug,
        CancellationToken ct)
    {
        var tools = await GetToolsAsync(server, ct).ConfigureAwait(false);
        return tools.Find(toolSlug);
    }

    /// <summary>Call one tool. Failures come back as the result, not as exceptions:
    /// the model reads them and decides what to do next.</summary>
    public async Task<string> CallToolAsync(
        McpServerOptions server,
        McpToolDescriptor tool,
        string argumentsJson,
        CancellationToken ct)
    {
        var connection = ConnectionFor(server);
        try
        {
            var session = await ClientFor(server, connection).WaitAsync(ct).ConfigureAwait(false);
            var arguments = ParseArguments(argumentsJson);
            var result = await session.Client.CallToolAsync(tool.Name, arguments, cancellationToken: ct).ConfigureAwait(false);
            return FormatResult(server, tool, result);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (McpProtocolException ex)
        {
            // The server answered with an error; the connection is fine.
            return FormatFailure(server, tool, ex.Message);
        }
        catch (Exception ex)
        {
            // Not retried: the server may already have run the call. The next call
            // reconnects.
            Drop(server.Id, connection);
            return FormatFailure(server, tool, Describe(ex));
        }
    }

    /// <summary>Connect with <paramref name="server"/> as entered, list its tools
    /// and disconnect. Independent of the shared connection, so an unsaved edit
    /// can be tried without disturbing conversations using the saved one.</summary>
    public async Task<McpServerTools> ProbeAsync(McpServerOptions server, CancellationToken ct)
    {
        await using var session = await OpenAsync(server, onToolsChanged: null, ct).ConfigureAwait(false);
        return await ReadToolsAsync(session.Client, ct).ConfigureAwait(false);
    }

    /// <summary>Disconnect a server that was removed or switched off. A stdio
    /// server's process ends with it.</summary>
    public Task CloseAsync(string serverId) =>
        _connections.TryRemove(serverId, out var connection)
            ? DisposeAsync(connection)
            : Task.CompletedTask;

    private Connection ConnectionFor(McpServerOptions server)
    {
        var fingerprint = Fingerprint(server);
        while (true)
        {
            var existing = _connections.GetOrAdd(server.Id, _ => new Connection(fingerprint));
            if (existing.Fingerprint == fingerprint) return existing;

            // The configuration changed: the old connection is for a server that
            // no longer exists as configured.
            if (_connections.TryUpdate(server.Id, new Connection(fingerprint), existing))
                _ = DisposeAsync(existing);
        }
    }

    private Task<Session> ClientFor(McpServerOptions server, Connection connection)
    {
        lock (connection.Gate)
        {
            var client = connection.Client;
            var usable = client is not null
                         && (!client.IsCompleted
                             || client.IsCompletedSuccessfully
                             || DateTimeOffset.UtcNow - connection.FailedAt < RetryAfter);
            if (!usable) connection.Client = client = ConnectAsync(server, connection);
            return client!;
        }
    }

    /// <summary>Runs on the manager's lifetime rather than the caller's token: a
    /// turn that is stopped mid-connect must not leave a cancelled connection
    /// behind for every later turn to trip over.</summary>
    private async Task<Session> ConnectAsync(McpServerOptions server, Connection connection)
    {
        var limit = server.IsStdio ? StdioConnectTimeout : HttpConnectTimeout;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        timeout.CancelAfter(limit);
        try
        {
            return await OpenAsync(
                server,
                () =>
                {
                    lock (connection.Gate) connection.Tools = null;
                },
                timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!_lifetime.IsCancellationRequested)
        {
            lock (connection.Gate) connection.FailedAt = DateTimeOffset.UtcNow;
            throw new TimeoutException($"{limit.TotalSeconds:0} 秒内未完成连接。");
        }
        catch
        {
            lock (connection.Gate) connection.FailedAt = DateTimeOffset.UtcNow;
            throw;
        }
    }

    private async Task<McpServerTools> ListToolsAsync(McpServerOptions server, Connection connection)
    {
        var session = await ClientFor(server, connection).ConfigureAwait(false);
        try
        {
            return await ReadToolsAsync(session.Client, _lifetime.Token).ConfigureAwait(false);
        }
        catch (McpProtocolException)
        {
            throw;
        }
        catch
        {
            Drop(server.Id, connection);
            throw;
        }
    }

    private static async Task<McpServerTools> ReadToolsAsync(McpClient client, CancellationToken ct)
    {
        // The SDK follows nextCursor, so this is every page.
        var listed = await client.ListToolsAsync(cancellationToken: ct).ConfigureAwait(false);
        var tools = listed
            .Select(tool => tool.ProtocolTool)
            .Select(tool => new McpToolDescriptor(
                tool.Name,
                tool.Description,
                tool.InputSchema.Clone(),
                Capabilities(tool.Annotations),
                tool.Annotations is { } hints
                    ? new McpToolHints(hints.ReadOnlyHint, hints.DestructiveHint, hints.IdempotentHint, hints.OpenWorldHint)
                    : null,
                tool.OutputSchema?.Clone()))
            .ToArray();
        return new McpServerTools(tools, client.ServerInstructions);
    }

    /// <summary>
    /// MCP annotations are optional hints. A tool without them fails closed as an
    /// externally connected write operation until its server declares it read-only.
    /// </summary>
    private static ToolCapability Capabilities(ToolAnnotations? hints)
    {
        var capabilities = ToolCapability.External;
        capabilities |= hints?.ReadOnlyHint == true ? ToolCapability.Read : ToolCapability.Write;
        if (hints?.DestructiveHint == true) capabilities |= ToolCapability.Destructive;
        return capabilities;
    }

    private void Drop(string serverId, Connection connection)
    {
        if (_connections.TryRemove(KeyValuePair.Create(serverId, connection)))
            _ = DisposeAsync(connection);
    }

    private async Task DisposeAsync(Connection connection)
    {
        Task<Session>? opening;
        lock (connection.Gate) opening = connection.Client;
        if (opening is null) return;
        Session session;
        try
        {
            session = await opening.ConfigureAwait(false);
        }
        catch
        {
            // Never came up; OpenAsync already cleaned up after itself.
            return;
        }
        await session.DisposeAsync().ConfigureAwait(false);
    }

    /// <summary>A connected client and, for stdio, the process behind it.</summary>
    private sealed class Session(McpClient client, McpStdioProcess? process, Action<string>? log) : IAsyncDisposable
    {
        public McpClient Client { get; } = client;

        public async ValueTask DisposeAsync()
        {
            try { await Client.DisposeAsync().ConfigureAwait(false); }
            catch (Exception ex) { log?.Invoke("[mcp] 断开连接失败：" + ex.Message); }
            if (process is not null) await process.DisposeAsync().ConfigureAwait(false);
        }
    }

    private async Task<Session> OpenAsync(McpServerOptions server, Action? onToolsChanged, CancellationToken ct)
    {
        if (!server.IsStdio)
        {
            var client = await McpClient.CreateAsync(CreateHttpTransport(server), CreateOptions(onToolsChanged), cancellationToken: ct)
                .ConfigureAwait(false);
            return new Session(client, null, _log);
        }

        var fingerprint = Fingerprint(server);
        if (_initializeOnly.ContainsKey(fingerprint))
            return await OpenStdioAsync(server, initializeOnly: true, onToolsChanged, ct).ConfigureAwait(false);
        try
        {
            return await OpenStdioAsync(server, initializeOnly: false, onToolsChanged, ct).ConfigureAwait(false);
        }
        catch (ServerExitedException ex)
        {
            // Possibly a server that exits on the discover probe; once more without it.
            _log?.Invoke($"[mcp] {server.Name}：握手时进程退出，改用 initialize 重试。{ex.Message}");
            var session = await OpenStdioAsync(server, initializeOnly: true, onToolsChanged, ct).ConfigureAwait(false);
            _initializeOnly[fingerprint] = 0;
            return session;
        }
    }

    private async Task<Session> OpenStdioAsync(
        McpServerOptions server,
        bool initializeOnly,
        Action? onToolsChanged,
        CancellationToken ct)
    {
        var process = McpStdioProcess.Start(server, _log);
        try
        {
            IClientTransport transport = new StreamClientTransport(process.Input, process.Output);
            if (initializeOnly) transport = new InitializeOnlyTransport(transport);
            var client = await McpClient.CreateAsync(transport, CreateOptions(onToolsChanged), cancellationToken: ct)
                .ConfigureAwait(false);
            return new Session(client, process, _log);
        }
        catch (Exception ex)
        {
            // A server that exits during the handshake explains itself on stderr;
            // the transport only knows that the stream closed.
            var exit = await process.DescribeExitAsync().ConfigureAwait(false);
            await process.DisposeAsync().ConfigureAwait(false);
            if (exit is not null && ex is not OperationCanceledException) throw new ServerExitedException(exit, ex);
            throw;
        }
    }

    private sealed class ServerExitedException(string message, Exception inner)
        : InvalidOperationException(message, inner);

    private McpClientOptions CreateOptions(Action? onToolsChanged) => new()
    {
        ClientInfo = new Implementation { Name = "MolaGPT Desktop", Version = ClientVersion },
        Handlers = new McpClientHandlers
        {
            NotificationHandlers = onToolsChanged is null
                ? null
                :
                [
                    new(NotificationMethods.ToolListChangedNotification, (_, _) =>
                    {
                        onToolsChanged();
                        return ValueTask.CompletedTask;
                    })
                ]
        }
    };

    private static readonly string ClientVersion =
        typeof(McpClientManager).Assembly.GetName().Version?.ToString(3) ?? "1.0.0";

    private IClientTransport CreateHttpTransport(McpServerOptions server)
    {
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (!string.IsNullOrWhiteSpace(server.Token))
        {
            var header = string.IsNullOrWhiteSpace(server.HeaderName) ? "Authorization" : server.HeaderName.Trim();
            var value = server.Token.Trim();
            if (string.Equals(header, "Authorization", StringComparison.OrdinalIgnoreCase)
                && !value.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
                value = "Bearer " + value;
            headers[header] = value;
        }

        return new HttpClientTransport(
            new HttpClientTransportOptions
            {
                Name = server.Name,
                Endpoint = new Uri(server.Url),
                // Streamable HTTP first, the older SSE transport if the server only has that.
                TransportMode = HttpTransportMode.AutoDetect,
                AdditionalHeaders = headers,
            },
            _http,
            ownsHttpClient: false);
    }

    private static string Fingerprint(McpServerOptions server) => JsonSerializer.Serialize(new
    {
        server.Transport,
        server.Url,
        server.HeaderName,
        server.Token,
        server.Command,
        server.Arguments,
        server.WorkingDirectory,
        Environment = server.Environment?.OrderBy(pair => pair.Key, StringComparer.Ordinal).ToArray(),
    });

    private static IReadOnlyDictionary<string, object?> ParseArguments(string argumentsJson)
    {
        using var document = JsonDocument.Parse(string.IsNullOrWhiteSpace(argumentsJson) ? "{}" : argumentsJson);
        if (document.RootElement.ValueKind != JsonValueKind.Object)
            throw new ArgumentException("工具参数必须是 JSON 对象。");
        return document.RootElement.EnumerateObject()
            .ToDictionary(property => property.Name, object? (property) => property.Value.Clone(), StringComparer.Ordinal);
    }

    /// <summary>
    /// The model-facing result. Text is joined into one field; binary content is
    /// named but not inlined, since a base64 image in a text result only costs
    /// context. <c>structuredContent</c> is kept whole for codemode scripts.
    /// </summary>
    private static string FormatResult(McpServerOptions server, McpToolDescriptor tool, CallToolResult result)
    {
        var text = new StringBuilder();
        var omitted = new List<string>();
        foreach (var block in result.Content)
        {
            switch (block)
            {
                case TextContentBlock textBlock:
                    AppendLine(text, textBlock.Text);
                    break;
                case EmbeddedResourceBlock { Resource: TextResourceContents resource }:
                    AppendLine(text, resource.Text);
                    break;
                case EmbeddedResourceBlock { Resource: { } resource }:
                    omitted.Add($"resource {resource.Uri} ({resource.MimeType})");
                    break;
                case ResourceLinkBlock link:
                    AppendLine(text, $"[{link.Name}]({link.Uri})");
                    break;
                case ImageContentBlock image:
                    omitted.Add($"image ({image.MimeType})");
                    break;
                case AudioContentBlock audio:
                    omitted.Add($"audio ({audio.MimeType})");
                    break;
                default:
                    omitted.Add(block.Type);
                    break;
            }
        }

        var failed = result.IsError == true;
        return JsonSerializer.Serialize(new McpCallResult(
            Success: !failed,
            Server: server.Name,
            Tool: tool.Name,
            Text: text.ToString(),
            Error: failed ? (text.Length > 0 ? text.ToString() : "MCP 工具返回了错误。") : null,
            StructuredContent: result.StructuredContent,
            Omitted: omitted.Count > 0 ? omitted : null), ResultJson);
    }

    private static string FormatFailure(McpServerOptions server, McpToolDescriptor tool, string message) =>
        JsonSerializer.Serialize(new McpCallResult(false, server.Name, tool.Name, string.Empty, message, null, null), ResultJson);

    private static void AppendLine(StringBuilder text, string value)
    {
        if (text.Length > 0) text.Append('\n');
        text.Append(value);
    }

    private sealed record McpCallResult(
        bool Success,
        string Server,
        string Tool,
        string Text,
        string? Error,
        JsonElement? StructuredContent,
        IReadOnlyList<string>? Omitted);

    private static readonly JsonSerializerOptions ResultJson = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>The JSON Schema of every MCP call result, as codemode scripts receive
    /// it; <paramref name="structured"/> is the tool's own output schema.</summary>
    public static JsonObject ResultSchema(JsonElement? structured) => new()
    {
        ["type"] = "object",
        ["properties"] = new JsonObject
        {
            ["success"] = new JsonObject { ["type"] = "boolean" },
            ["text"] = new JsonObject { ["type"] = "string" },
            ["error"] = new JsonObject { ["type"] = "string" },
            ["structuredContent"] = structured is { ValueKind: JsonValueKind.Object } schema
                ? JsonNode.Parse(schema.GetRawText())
                : new JsonObject(),
        },
        ["required"] = new JsonArray("success", "text"),
    };

    /// <summary>The outermost message: wrappers here add what the inner one lacks,
    /// such as a process's stderr.</summary>
    private static string Describe(Exception ex) => ex switch
    {
        AggregateException { InnerException: { } inner } => Describe(inner),
        HttpRequestException http when http.StatusCode is { } status => $"HTTP {(int)status}：{http.Message}",
        _ => ex.Message,
    };

    private int _disposed;

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _lifetime.Cancel();
        var closing = _connections.Values.ToArray();
        _connections.Clear();
        await Task.WhenAll(closing.Select(DisposeAsync)).ConfigureAwait(false);
        _lifetime.Dispose();
    }
}

/// <summary>What a server offers: its tools and the instructions it sent with them.</summary>
public sealed record McpServerTools(IReadOnlyList<McpToolDescriptor> Tools, string? Instructions)
{
    public McpToolDescriptor? Find(string toolSlug) =>
        Tools.FirstOrDefault(tool => string.Equals(McpToolName.Slugify(tool.Name), toolSlug, StringComparison.Ordinal));
}

public sealed record McpToolDescriptor(
    string Name,
    string? Description,
    JsonElement InputSchema,
    ToolCapability Capabilities,
    McpToolHints? Hints = null,
    JsonElement? OutputSchema = null);

/// <summary>The server's MCP annotations, unverified. Null means not declared.</summary>
public sealed record McpToolHints(
    bool? ReadOnly,
    bool? Destructive,
    bool? Idempotent,
    bool? OpenWorld);

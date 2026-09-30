using System.Threading.Channels;
using ModelContextProtocol;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace MolaGPT.Core.Chat.Tools.Mcp;

/// <summary>
/// A transport for stdio servers that exit on the SDK's <c>server/discover</c>
/// probe instead of answering it — those built on rmcp before 1.0 or on early
/// releases of the Python SDK, which accept nothing before <c>initialize</c>.
///
/// The SDK falls back to <c>initialize</c> on the same connection, which such a
/// server has already closed. Here the probe never reaches the server: it is
/// answered with "method not found", the answer the SDK takes to mean an
/// initialize-handshake server, and the version is then negotiated as usual.
/// Pinning <c>ProtocolVersion</c> instead would also skip the probe, but the SDK
/// then accepts only that exact version back.
/// </summary>
internal sealed class InitializeOnlyTransport(IClientTransport inner) : IClientTransport
{
    public string Name => inner.Name;

    public async Task<ITransport> ConnectAsync(CancellationToken cancellationToken = default) =>
        new Session(await inner.ConnectAsync(cancellationToken).ConfigureAwait(false));

    private sealed class Session : ITransport
    {
        private readonly ITransport _inner;
        private readonly Channel<JsonRpcMessage> _messages = Channel.CreateUnbounded<JsonRpcMessage>();
        private readonly Task _pump;

        public Session(ITransport inner)
        {
            _inner = inner;
            _pump = PumpAsync();
        }

        public string? SessionId => _inner.SessionId;
        public ChannelReader<JsonRpcMessage> MessageReader => _messages.Reader;

        public Task SendMessageAsync(JsonRpcMessage message, CancellationToken cancellationToken = default)
        {
            if (message is not JsonRpcRequest { Method: RequestMethods.ServerDiscover } probe)
                return _inner.SendMessageAsync(message, cancellationToken);

            _messages.Writer.TryWrite(new JsonRpcError
            {
                Id = probe.Id,
                Error = new JsonRpcErrorDetail { Code = (int)McpErrorCode.MethodNotFound, Message = "Method not found" },
            });
            return Task.CompletedTask;
        }

        /// <summary>The server's messages, and its end: a closed stream completes
        /// the reader, which is how the SDK learns the server is gone.</summary>
        private async Task PumpAsync()
        {
            try
            {
                await foreach (var message in _inner.MessageReader.ReadAllAsync().ConfigureAwait(false))
                    await _messages.Writer.WriteAsync(message).ConfigureAwait(false);
                _messages.Writer.TryComplete();
            }
            catch (Exception ex)
            {
                _messages.Writer.TryComplete(ex);
            }
        }

        public async ValueTask DisposeAsync()
        {
            await _inner.DisposeAsync().ConfigureAwait(false);
            await _pump.ConfigureAwait(false);
        }
    }
}

using System.Net.Http;
using System.Net.WebSockets;
using System.Text.Json;
using System.Threading.Channels;

namespace MolaGPT.Desktop.Services;

public sealed partial class HttpRelayProducer
{
    private volatile bool _realtimeConnected;

    private async Task RealtimeLoopAsync(ChannelWriter<bool> wake, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, _base + "/api/auth/agent_realtime.php");
                if (!TryAttachAuth(request)) throw new InvalidOperationException(AuthUnavailable);
                using var response = await _http.SendAsync(request, ct).ConfigureAwait(false);
                response.EnsureSuccessStatusCode();
                using var ticket = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
                using var socket = new ClientWebSocket();
                socket.Options.SetRequestHeader("Authorization", "Bearer " + ticket.RootElement.GetProperty("ticket").GetString());
                socket.Options.KeepAliveInterval = TimeSpan.FromSeconds(20);
                socket.Options.KeepAliveTimeout = TimeSpan.FromSeconds(20);
                var uri = new UriBuilder(_base + "/agent-realtime") { Scheme = new Uri(_base).Scheme == "https" ? "wss" : "ws" };
                await socket.ConnectAsync(uri.Uri, ct).ConfigureAwait(false);
                _realtimeConnected = true;
                var buffer = new byte[4096];
                while (socket.State == WebSocketState.Open)
                {
                    var result = await socket.ReceiveAsync(buffer.AsMemory(), ct).ConfigureAwait(false);
                    if (result.MessageType == WebSocketMessageType.Close) break;
                    if (!result.EndOfMessage) throw new InvalidDataException("Relay notification too large.");
                    using var notice = JsonDocument.Parse(buffer.AsMemory(0, result.Count));
                    if (notice.RootElement.GetProperty("kind").GetString() is "connected" or "commands") wake.TryWrite(true);
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch { /* Existing command polling continues during relay outages. */ }
            finally { _realtimeConnected = false; wake.TryWrite(true); }
            try { await Task.Delay(TimeSpan.FromSeconds(5), ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }
        }
    }
}

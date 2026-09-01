using System.Net;
using System.Net.Sockets;
using System.Text;

namespace Dsh.Web;

/// <summary>
/// The recorded web-search error fixture: a loopback Messages endpoint on the recorded authority
/// (<c>127.0.0.1:43118</c>) answering <c>POST /anthropic/v1/messages</c> with the recorded 401
/// <c>{"error":{"message":"invalid snapshot API key"}}</c> body. Node is not used in the ported
/// version; the endpoint is embedded.
/// </summary>
public sealed class FixtureWebSearchServer : IAsyncDisposable
{
    private readonly TcpListener _listener;
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _acceptLoop;

    /// <summary>Start the loopback endpoint on the recorded port; fails loud when it is taken.</summary>
    public FixtureWebSearchServer()
    {
        _listener = new TcpListener(IPAddress.Loopback, 43118);
        _listener.Start();
        _acceptLoop = AcceptLoopAsync(_stop.Token);
    }

    /// <summary>The recorded Messages endpoint this server answers.</summary>
    public static string RecordedEndpoint => "http://127.0.0.1:43118/anthropic/v1/messages";

    private async Task AcceptLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await _listener.AcceptTcpClientAsync(ct);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (SocketException)
            {
                return;
            }
            _ = ServeAsync(client);
        }
    }

    private static async Task ServeAsync(TcpClient client)
    {
        using (client)
        using (var stream = client.GetStream())
        {
            try
            {
                var request = await ReadHeadAsync(stream);
                var lines = request.Split("\r\n");
                var requestLine = lines.Length > 0 ? lines[0] : string.Empty;
                var parts = requestLine.Split(' ');
                var isMessages = parts.Length >= 2
                    && parts[0] == "POST"
                    && parts[1] == "/anthropic/v1/messages";
                var status = isMessages ? "401 Unauthorized" : "404 Not Found";
                var body = isMessages ? "{\"error\":{\"message\":\"invalid snapshot API key\"}}" : "not found";
                var bytes = Encoding.UTF8.GetBytes(body);
                var head = $"HTTP/1.1 {status}\r\ncontent-type: application/json\r\ncontent-length: {bytes.Length}\r\nconnection: close\r\n\r\n";
                await stream.WriteAsync(Encoding.ASCII.GetBytes(head).Concat(bytes).ToArray());
            }
            catch
            {
                // A torn or aborted client connection is not a server failure.
            }
        }
    }

    private static async Task<string> ReadHeadAsync(NetworkStream stream)
    {
        var buffer = new byte[4096];
        var received = 0;
        while (received < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(received, buffer.Length - received));
            if (read == 0) break;
            received += read;
            var text = Encoding.ASCII.GetString(buffer, 0, received);
            var end = text.IndexOf("\r\n\r\n", StringComparison.Ordinal);
            if (end >= 0) return text[..end];
        }
        return string.Empty;
    }

    /// <summary>Stop accepting and release the port.</summary>
    public async ValueTask DisposeAsync()
    {
        _stop.Cancel();
        try
        {
            _listener.Stop();
            await _acceptLoop;
        }
        catch
        {
            // The accept loop settles cancelled; nothing else can throw here.
        }
        _stop.Dispose();
    }
}
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace Harness.Web;

/// <summary>
/// The recorded web-fetch fixture server: a loopback HTTP server on the recorded authority
/// (<c>127.0.0.1:43117</c>) serving the exact recorded page (headings, named entities, a GFM
/// table, nested formatting) so the corpus exercises the real fetch + markdown pipeline without
/// external network. Node is not used in the ported version; the page is embedded verbatim.
/// </summary>
public sealed class FixtureWebFetchServer : IAsyncDisposable
{
    /// <summary>The recorded menu page source (verbatim from the recorded fixture server).</summary>
    public const string MenuPage =
        "<!doctype html>\n<html><head><title>Menu</title><style>.x{color:red}</style><script>ignored()</script></head>\n"
        + "<body>\n<h1>Caf&eacute; menu</h1>\n"
        + "<p>Prices include <strong>service &amp; <em>tax</em></strong> &mdash; updated daily.</p>\n"
        + "<ul><li>Espresso</li><li>Flat white</li></ul>\n"
        + "<table><thead><tr><th>Drink</th><th>Price</th></tr></thead><tbody><tr><td>Espresso</td><td>&euro;2</td></tr><tr><td>Flat white</td><td>&euro;3</td></tr></tbody></table>\n"
        + "<p>See <a href=\"https://fixture.invalid/specials\">today&rsquo;s specials</a>.</p>\n</body></html>\n";

    private readonly TcpListener _listener;
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _acceptLoop;

    /// <summary>Start the loopback server on the recorded port; fails loud when it is taken.</summary>
    public FixtureWebFetchServer()
    {
        _listener = new TcpListener(IPAddress.Loopback, 43117);
        _listener.Start();
        _acceptLoop = AcceptLoopAsync(_stop.Token);
    }

    /// <summary>The recorded authority this server serves.</summary>
    public static string RecordedUrl => "http://public.test:43117/menu.html";

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
                var path = request.Length > 0 ? request.Split(' ')[1] : string.Empty;
                var body = path == "/menu.html" ? MenuPage : null;
                var status = body is null ? "404 Not Found" : "200 OK";
                var contentType = body is null ? "text/plain; charset=utf-8" : "text/html; charset=utf-8";
                body ??= "not found";
                var bytes = Encoding.UTF8.GetBytes(body);
                var head = $"HTTP/1.1 {status}\r\ncontent-type: {contentType}\r\ncontent-length: {bytes.Length}\r\nconnection: close\r\n\r\n";
                var response = Encoding.ASCII.GetBytes(head).Concat(bytes).ToArray();
                await stream.WriteAsync(response);
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
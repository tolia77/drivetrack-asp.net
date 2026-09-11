using System.Net;
using System.Net.Sockets;
using System.Text;

namespace DriveTrack.Integration.Tests.Support;

/// <summary>
/// A one-file HTTP server on a loopback port, so an outbound adapter can be exercised against a
/// real socket without reaching the internet.
/// <para>
/// A <see cref="TcpListener"/> rather than <c>HttpListener</c>, and the reason is portability:
/// <c>HttpListener</c> needs a URL reservation on Windows, which is a machine-level privilege a test
/// run must not depend on. The protocol surface an adapter actually uses is a request line, a couple
/// of headers and a JSON body, and speaking that directly is both smaller and more honest — it lets
/// a test assert the exact bytes the adapter sent, which is the half a fake port can never cover.
/// </para>
/// </summary>
internal sealed class LoopbackHttpServer : IAsyncDisposable
{
    private readonly TcpListener _listener;
    private readonly CancellationTokenSource _stopping = new();
    private readonly Task _accepting;
    private readonly Lock _gate = new();
    private readonly List<RecordedRequest> _requests = [];
    private readonly Func<string, CannedResponse> _respond;

    private LoopbackHttpServer(TcpListener listener, Func<string, CannedResponse> respond)
    {
        _listener = listener;
        _respond = respond;
        _accepting = Task.Run(AcceptAsync);
    }

    /// <summary>One request as the server saw it.</summary>
    /// <param name="Target">The request target: the path and its query string, exactly as sent.</param>
    /// <param name="UserAgent">The agent header, or null when none was sent.</param>
    internal sealed record RecordedRequest(string Target, string? UserAgent);

    /// <summary>What to answer with.</summary>
    /// <param name="Status">The status code.</param>
    /// <param name="Body">The body, sent as <c>application/json</c>.</param>
    internal sealed record CannedResponse(int Status, string Body);

    /// <summary>Starts a server on a free loopback port.</summary>
    /// <param name="respond">Answers a request target with the response to send back.</param>
    public static LoopbackHttpServer Start(Func<string, CannedResponse> respond)
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();

        return new LoopbackHttpServer(listener, respond);
    }

    /// <summary>The server's base address, with a trailing slash.</summary>
    public string BaseAddress =>
        "http://127.0.0.1:"
        + ((IPEndPoint)_listener.LocalEndpoint).Port.ToString(System.Globalization.CultureInfo.InvariantCulture)
        + "/";

    /// <summary>Everything the server has been asked, in order.</summary>
    public IReadOnlyList<RecordedRequest> Requests
    {
        get
        {
            lock (_gate)
            {
                return [.. _requests];
            }
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        await _stopping.CancelAsync();
        _listener.Stop();

        try
        {
            await _accepting;
        }
        catch (Exception)
        {
            // The loop is being torn down; whatever it was waiting on is no longer interesting.
        }

        _stopping.Dispose();
    }

    private async Task AcceptAsync()
    {
        while (!_stopping.IsCancellationRequested)
        {
            TcpClient client;

            try
            {
                client = await _listener.AcceptTcpClientAsync(_stopping.Token);
            }
            catch (Exception)
            {
                return;
            }

            // Serve on its own task, so one connection cannot hold the next request up. Nothing
            // awaits it: the assertion is about what the adapter received back, and that arrives
            // through the adapter.
            _ = ServeAsync(client);
        }
    }

    private async Task ServeAsync(TcpClient client)
    {
        try
        {
            using (client)
            {
                await using var stream = client.GetStream();

                var head = await ReadHeadAsync(stream);

                if (head is null)
                {
                    return;
                }

                var lines = head.Split("\r\n");
                var target = lines[0].Split(' ') is [_, var requested, ..] ? requested : string.Empty;

                var agent = lines
                    .Skip(1)
                    .FirstOrDefault(line => line.StartsWith("User-Agent:", StringComparison.OrdinalIgnoreCase))
                    ?["User-Agent:".Length..]
                    .Trim();

                lock (_gate)
                {
                    _requests.Add(new RecordedRequest(target, agent));
                }

                var answer = _respond(target);
                var body = Encoding.UTF8.GetBytes(answer.Body);

                var header =
                    "HTTP/1.1 " + answer.Status.ToString(System.Globalization.CultureInfo.InvariantCulture)
                    + " S\r\n"
                    + "Content-Type: application/json; charset=utf-8\r\n"
                    + "Content-Length: "
                    + body.Length.ToString(System.Globalization.CultureInfo.InvariantCulture) + "\r\n"
                    + "Connection: close\r\n\r\n";

                await stream.WriteAsync(Encoding.ASCII.GetBytes(header));
                await stream.WriteAsync(body);
                await stream.FlushAsync();
            }
        }
        catch (Exception)
        {
            // A client that hung up mid-exchange is the adapter's business, not the server's.
        }
    }

    /// <summary>The request head, or null when the connection closed before one arrived.</summary>
    private static async Task<string?> ReadHeadAsync(NetworkStream stream)
    {
        var buffer = new byte[1024];
        var head = new StringBuilder();

        while (!head.ToString().Contains("\r\n\r\n", StringComparison.Ordinal))
        {
            var read = await stream.ReadAsync(buffer);

            if (read == 0)
            {
                return null;
            }

            head.Append(Encoding.ASCII.GetString(buffer, 0, read));
        }

        return head.ToString();
    }
}

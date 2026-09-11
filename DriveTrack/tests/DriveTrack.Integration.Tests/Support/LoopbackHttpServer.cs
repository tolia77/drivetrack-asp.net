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
    private readonly Func<RecordedRequest, CannedResponse> _respond;

    private LoopbackHttpServer(TcpListener listener, Func<RecordedRequest, CannedResponse> respond)
    {
        _listener = listener;
        _respond = respond;
        _accepting = Task.Run(AcceptAsync);
    }

    /// <summary>
    /// One request as the server saw it.
    /// <para>
    /// The whole head and the whole body, not a chosen field or two. An outbound adapter is only
    /// worth testing against a socket if the bytes it sent can be read back, and the object-store
    /// adapter's three load-bearing settings — path style, the signing region, chunk encoding — are
    /// each visible in a different part of the request: the path, the <c>Authorization</c> header's
    /// credential scope, and the body's framing.
    /// </para>
    /// </summary>
    /// <param name="Method">The request method.</param>
    /// <param name="Target">The request target: the path and its query string, exactly as sent.</param>
    /// <param name="Headers">Every header, by name, with the casing the sender used.</param>
    /// <param name="Body">The body bytes, empty when the request carried none.</param>
    internal sealed record RecordedRequest(
        string Method,
        string Target,
        IReadOnlyDictionary<string, string> Headers,
        byte[] Body)
    {
        /// <summary>The agent header, or null when none was sent.</summary>
        public string? UserAgent => Header("User-Agent");

        /// <summary>The body as text, which is what a JSON or a signed-string assertion wants.</summary>
        public string BodyText => Encoding.UTF8.GetString(Body);

        /// <summary>One header by name, case-insensitively, or null when it was not sent.</summary>
        /// <param name="name">The header name.</param>
        public string? Header(string name) => Headers.TryGetValue(name, out var value) ? value : null;
    }

    /// <summary>What to answer with.</summary>
    /// <param name="Status">The status code.</param>
    /// <param name="Body">The body.</param>
    /// <param name="ContentType">
    /// The content type to answer with. JSON by default, because that is what the geocoder this
    /// server was first written for speaks; an object store answers bytes of any type at all.
    /// </param>
    internal sealed record CannedResponse(
        int Status,
        string Body,
        string ContentType = "application/json; charset=utf-8");

    /// <summary>
    /// Starts a server on a free loopback port.
    /// <para>
    /// The responder is handed the whole request rather than its target alone, because a store's
    /// read and its write share a path and only the method tells them apart. A caller that does not
    /// care writes <c>_ =&gt;</c> and is unaffected.
    /// </para>
    /// </summary>
    /// <param name="respond">Answers a recorded request with the response to send back.</param>
    public static LoopbackHttpServer Start(Func<RecordedRequest, CannedResponse> respond)
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

                var request = await ReadRequestAsync(stream);

                if (request is null)
                {
                    return;
                }

                lock (_gate)
                {
                    _requests.Add(request);
                }

                var answer = _respond(request);
                var body = Encoding.UTF8.GetBytes(answer.Body);

                var header =
                    "HTTP/1.1 " + answer.Status.ToString(System.Globalization.CultureInfo.InvariantCulture)
                    + " S\r\n"
                    + "Content-Type: " + answer.ContentType + "\r\n"
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

    /// <summary>
    /// The whole request, or null when the connection closed before one arrived.
    /// </summary>
    /// <remarks>
    /// Read as bytes rather than as text, and that is not fussiness: a proof asset is a PNG, and
    /// decoding the body as ASCII on the way in would corrupt exactly the bytes a round-trip
    /// assertion is about. The head is ASCII by the protocol's own rules, so only it is decoded.
    /// </remarks>
    private static async Task<RecordedRequest?> ReadRequestAsync(NetworkStream stream)
    {
        var received = new MemoryStream();
        var buffer = new byte[8192];
        var separator = -1;

        while (separator < 0)
        {
            var read = await stream.ReadAsync(buffer);

            if (read == 0)
            {
                return null;
            }

            received.Write(buffer, 0, read);
            separator = IndexOfHeadEnd(received.GetBuffer(), (int)received.Length);
        }

        var head = Encoding.ASCII.GetString(received.GetBuffer(), 0, separator);
        var lines = head.Split("\r\n");
        var start = lines[0].Split(' ');

        var method = start.Length > 0 ? start[0] : string.Empty;
        var target = start.Length > 1 ? start[1] : string.Empty;

        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var line in lines.Skip(1))
        {
            var colon = line.IndexOf(':', StringComparison.Ordinal);

            if (colon > 0)
            {
                headers[line[..colon].Trim()] = line[(colon + 1)..].Trim();
            }
        }

        // An expectation answered rather than ignored. HttpClient sends `Expect: 100-continue` for
        // a request with a body and then waits for a response before sending one; a server that
        // never answers makes every upload pay the client's timeout, which turns a fast test into a
        // slow one for no reason a reader would ever guess.
        if (headers.TryGetValue("Expect", out var expect)
            && expect.Contains("100-continue", StringComparison.OrdinalIgnoreCase))
        {
            await stream.WriteAsync(Encoding.ASCII.GetBytes("HTTP/1.1 100 Continue\r\n\r\n"));
            await stream.FlushAsync();
        }

        var bodyStart = separator + 4;
        var length = headers.TryGetValue("Content-Length", out var declared)
            && int.TryParse(declared, System.Globalization.CultureInfo.InvariantCulture, out var parsed)
                ? parsed
                : 0;

        var body = new MemoryStream();

        body.Write(received.GetBuffer(), bodyStart, (int)received.Length - bodyStart);

        while (body.Length < length)
        {
            var read = await stream.ReadAsync(buffer);

            if (read == 0)
            {
                break;
            }

            body.Write(buffer, 0, read);
        }

        return new RecordedRequest(method, target, headers, body.ToArray());
    }

    /// <summary>The offset of the blank line ending the head, or −1 while it has not arrived.</summary>
    private static int IndexOfHeadEnd(byte[] bytes, int length)
    {
        for (var index = 0; index + 3 < length; index++)
        {
            if (bytes[index] == (byte)'\r'
                && bytes[index + 1] == (byte)'\n'
                && bytes[index + 2] == (byte)'\r'
                && bytes[index + 3] == (byte)'\n')
            {
                return index;
            }
        }

        return -1;
    }
}

using System.Net;
using System.Net.Sockets;
using System.Text;

var options = TestHostOptions.Parse(args);
if (!string.Equals(options.Host, "127.0.0.1", StringComparison.Ordinal) ||
    !options.DisableUi ||
    !options.LocalFilesOnly)
{
    return 64;
}

Emit(Console.Out, 'O', options.StandardOutputCharacters);
Emit(Console.Error, 'E', options.StandardErrorCharacters);

using var lifetime = new CancellationTokenSource();
var listener = new TcpListener(IPAddress.Loopback, options.Port);
listener.Start();
using var stopListenerRegistration = lifetime.Token.Register(listener.Stop);
var startedAt = DateTimeOffset.UtcNow;

try
{
    while (!lifetime.IsCancellationRequested)
    {
        var client = await listener.AcceptTcpClientAsync(lifetime.Token);
        _ = HandleAsync(client, options, startedAt, lifetime);
    }
}
catch (OperationCanceledException)
{
}
catch (SocketException) when (lifetime.IsCancellationRequested)
{
}
catch (ObjectDisposedException) when (lifetime.IsCancellationRequested)
{
}
finally
{
    listener.Stop();
}

return 0;

static async Task HandleAsync(
    TcpClient client,
    TestHostOptions options,
    DateTimeOffset startedAt,
    CancellationTokenSource lifetime)
{
    await using var stream = client.GetStream();
    using var reader = new StreamReader(
        stream,
        Encoding.ASCII,
        detectEncodingFromByteOrderMarks: false,
        bufferSize: 1024,
        leaveOpen: true);
    var requestLine = await reader.ReadLineAsync();
    string? line;
    do
    {
        line = await reader.ReadLineAsync();
    }
    while (!string.IsNullOrEmpty(line));

    var ready = !options.NeverReady && DateTimeOffset.UtcNow - startedAt >= options.ReadyDelay;
    var shutdown = requestLine?.StartsWith("POST /shutdown ", StringComparison.Ordinal) == true;
    var health = requestLine?.StartsWith("GET /health ", StringComparison.Ordinal) == true;
    var status = health && ready || shutdown ? "200 OK" : "503 Service Unavailable";
    var response = Encoding.ASCII.GetBytes(
        $"HTTP/1.1 {status}\r\nContent-Length: 0\r\nConnection: close\r\n\r\n");
    await stream.WriteAsync(response);
    await stream.FlushAsync();
    client.Dispose();

    if (shutdown && !options.IgnoreShutdown)
    {
        lifetime.Cancel();
    }
}

static void Emit(TextWriter writer, char character, int count)
{
    var buffer = new string(character, 4096);
    while (count > 0)
    {
        var length = Math.Min(count, buffer.Length);
        writer.Write(buffer.AsSpan(0, length));
        count -= length;
    }

    writer.WriteLine("-END");
    writer.Flush();
}

internal sealed record TestHostOptions(
    string Host,
    int Port,
    bool DisableUi,
    bool LocalFilesOnly,
    TimeSpan ReadyDelay,
    bool NeverReady,
    bool IgnoreShutdown,
    int StandardOutputCharacters,
    int StandardErrorCharacters)
{
    internal static TestHostOptions Parse(string[] arguments)
    {
        var values = new Dictionary<string, string?>(StringComparer.Ordinal);
        for (var index = 0; index < arguments.Length; index++)
        {
            var argument = arguments[index];
            if (!argument.StartsWith("--", StringComparison.Ordinal))
            {
                continue;
            }

            if (argument is "--disable-ui" or "--local-files-only" or "--never-ready" or "--ignore-shutdown")
            {
                values[argument] = null;
            }
            else if (index + 1 < arguments.Length)
            {
                values[argument] = arguments[++index];
            }
        }

        return new(
            values.GetValueOrDefault("--host") ?? string.Empty,
            ParseInt(values, "--port"),
            values.ContainsKey("--disable-ui"),
            values.ContainsKey("--local-files-only"),
            TimeSpan.FromMilliseconds(ParseInt(values, "--ready-delay-ms")),
            values.ContainsKey("--never-ready"),
            values.ContainsKey("--ignore-shutdown"),
            ParseInt(values, "--stdout-characters"),
            ParseInt(values, "--stderr-characters"));
    }

    private static int ParseInt(IReadOnlyDictionary<string, string?> values, string key) =>
        int.TryParse(
            values.GetValueOrDefault(key),
            System.Globalization.CultureInfo.InvariantCulture,
            out var value)
            ? value
            : 0;
}

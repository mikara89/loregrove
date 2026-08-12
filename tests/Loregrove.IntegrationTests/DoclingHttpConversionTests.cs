using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Loregrove.Application.Parsing;
using Loregrove.Infrastructure.Docling;

namespace Loregrove.IntegrationTests;

public sealed class DoclingHttpConversionTests
{
    [Fact]
    public async Task AsyncMultipartPollingAndResultUseRealLoopbackHttp()
    {
        var sourceBytes = Encoding.UTF8.GetBytes("immutable-source-bytes");
        await using var server = await DoclingLoopbackServer.StartAsync(
            result: SuccessfulResult("success"),
            apiKey: "secret-key");
        using var client = CreateClient();
        await using var source = new MemoryStream(sourceBytes);

        var result = await client.ConvertAsync(
            server.Endpoint,
            Request(source, "safe.pdf", "secret-key"),
            isLeaseValid: null,
            CancellationToken.None);

        Assert.Equal(DoclingConversionStatus.Success, result.Status);
        Assert.Equal(sourceBytes, server.UploadBytes);
        Assert.Equal("safe.pdf", server.FileName);
        Assert.Equal("secret-key", server.SubmitApiKey);
        Assert.Equal("secret-key", server.PollApiKey);
        Assert.Equal("secret-key", server.ResultApiKey);
        Assert.True(server.PollCount >= 2);
        Assert.Contains("name=files", server.RawSubmitBody, StringComparison.Ordinal);
        Assert.Contains("name=to_formats", server.RawSubmitBody, StringComparison.Ordinal);
        Assert.Contains("json", server.RawSubmitBody, StringComparison.Ordinal);
        Assert.Contains("md", server.RawSubmitBody, StringComparison.Ordinal);
        Assert.Contains("name=ocr_preset", server.RawSubmitBody, StringComparison.Ordinal);
        Assert.DoesNotContain("name=ocr_engine", server.RawSubmitBody, StringComparison.Ordinal);
        Assert.DoesNotContain("processing_time", result.StructuredJson, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PartialSuccessAndDocumentFailureRemainDistinct()
    {
        await using var partialServer = await DoclingLoopbackServer.StartAsync(SuccessfulResult("partial_success"));
        using var client = CreateClient();
        await using var partialSource = new MemoryStream([1, 2, 3]);
        var partial = await client.ConvertAsync(
            partialServer.Endpoint,
            Request(partialSource, "partial.pdf"),
            null,
            CancellationToken.None);
        Assert.Equal(DoclingConversionStatus.PartialSuccess, partial.Status);
        Assert.Equal(1, partial.WarningCount);

        await using var failureServer = await DoclingLoopbackServer.StartAsync("""
            {"status":"failure","document":{"md_content":"","json_content":{}},"errors":[{"message":"private path"}]}
            """);
        await using var failureSource = new MemoryStream([4, 5, 6]);
        var failure = await client.ConvertAsync(
            failureServer.Endpoint,
            Request(failureSource, "failure.pdf"),
            null,
            CancellationToken.None);
        Assert.Equal(DoclingConversionStatus.DocumentFailure, failure.Status);
        Assert.Equal("docling-conversion-failed", failure.SafeDiagnosticCode);
    }

    [Fact]
    public async Task RedirectDoesNotForwardDocumentOrCredential()
    {
        await using var destination = await DoclingLoopbackServer.StartAsync(SuccessfulResult("success"));
        await using var redirect = await DoclingLoopbackServer.StartRedirectAsync(destination.Endpoint);
        using var client = CreateClient();
        await using var source = new MemoryStream(Encoding.UTF8.GetBytes("private bytes"));

        var exception = await Assert.ThrowsAsync<ParserInfrastructureException>(() => client.ConvertAsync(
            redirect.Endpoint,
            Request(source, "source.pdf", "private-key"),
            null,
            CancellationToken.None));

        Assert.Equal(ParserInfrastructureFailureCode.ApiIncompatible, exception.Code);
        Assert.Empty(destination.UploadBytes);
        Assert.Null(destination.SubmitApiKey);
    }

    [Fact]
    public async Task OversizedAndMalformedResponsesFailSafely()
    {
        await using var oversized = await DoclingLoopbackServer.StartAsync(
            "{\"status\":\"success\",\"padding\":\"" + new string('x', 4096) + "\"}");
        using var smallClient = new DoclingV1ApiClient(new DoclingConversionOptions
        {
            PollInterval = TimeSpan.FromMilliseconds(1),
            MaximumResponseBytes = 1024,
        });
        await using var source = new MemoryStream([1]);
        var tooLarge = await Assert.ThrowsAsync<ParserInfrastructureException>(() => smallClient.ConvertAsync(
            oversized.Endpoint,
            Request(source, "large.pdf"),
            null,
            CancellationToken.None));
        Assert.Equal(ParserInfrastructureFailureCode.ResponseTooLarge, tooLarge.Code);

        await using var malformed = await DoclingLoopbackServer.StartAsync("not-json");
        using var client = CreateClient();
        await using var malformedSource = new MemoryStream([2]);
        var incompatible = await Assert.ThrowsAsync<ParserInfrastructureException>(() => client.ConvertAsync(
            malformed.Endpoint,
            Request(malformedSource, "bad.pdf"),
            null,
            CancellationToken.None));
        Assert.Equal(ParserInfrastructureFailureCode.ApiIncompatible, incompatible.Code);
    }

    private static DoclingV1ApiClient CreateClient() => new(new DoclingConversionOptions
    {
        SubmitTimeout = TimeSpan.FromSeconds(3),
        PollRequestTimeout = TimeSpan.FromSeconds(3),
        PollInterval = TimeSpan.FromMilliseconds(5),
        OverallTimeout = TimeSpan.FromSeconds(5),
        ResultTimeout = TimeSpan.FromSeconds(3),
        MaximumResponseBytes = 1024 * 1024,
    });

    private static DoclingConversionRequest Request(Stream stream, string fileName, string? apiKey = null) => new(
        stream,
        fileName,
        "application/pdf",
        "pdf",
        DoclingConversionProfile.Conservative,
        apiKey);

    private static string SuccessfulResult(string status) => $$"""
        {
          "status":"{{status}}",
          "document":{
            "md_content":"# Evidence\r\n",
            "json_content":{
              "schema_name":"DoclingDocument",
              "version":"1.0.0",
              "body":{"self_ref":"#/body","children":[{"$ref":"#/texts/0"}]},
              "furniture":{"self_ref":"#/furniture","children":[]},
              "groups":[],"tables":[],"pictures":[],"key_value_items":[],
              "texts":[{"self_ref":"#/texts/0","label":"paragraph","text":"Evidence","children":[],"prov":[]}]
            }
          },
          "processing_time":42,
          "errors":[{"message":"safe count only"}]
        }
        """;

    private sealed class DoclingLoopbackServer : IAsyncDisposable
    {
        private readonly TcpListener _listener;
        private readonly CancellationTokenSource _lifetime = new();
        private readonly Task _runTask;
        private readonly string _result;
        private readonly Uri? _redirect;
        private readonly List<byte> _uploadBytes = [];
        private int _pollCount;

        private DoclingLoopbackServer(TcpListener listener, string result, Uri? redirect)
        {
            _listener = listener;
            _result = result;
            _redirect = redirect;
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            Endpoint = new Uri($"http://127.0.0.1:{port}/");
            _runTask = RunAsync();
        }

        internal Uri Endpoint { get; }
        internal byte[] UploadBytes => _uploadBytes.ToArray();
        internal string? FileName { get; private set; }
        internal string? RawSubmitBody { get; private set; }
        internal string? SubmitApiKey { get; private set; }
        internal string? PollApiKey { get; private set; }
        internal string? ResultApiKey { get; private set; }
        internal int PollCount => Volatile.Read(ref _pollCount);

        internal static Task<DoclingLoopbackServer> StartAsync(string result, string? apiKey = null) =>
            StartCoreAsync(result, redirect: null, apiKey);

        internal static Task<DoclingLoopbackServer> StartRedirectAsync(Uri destination) =>
            StartCoreAsync(string.Empty, destination, expectedApiKey: null);

        private static Task<DoclingLoopbackServer> StartCoreAsync(string result, Uri? redirect, string? expectedApiKey)
        {
            _ = expectedApiKey;
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            return Task.FromResult(new DoclingLoopbackServer(listener, result, redirect));
        }

        private async Task RunAsync()
        {
            try
            {
                while (!_lifetime.IsCancellationRequested)
                {
                    using var client = await _listener.AcceptTcpClientAsync(_lifetime.Token);
                    await HandleAsync(client);
                }
            }
            catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
            {
            }
            catch (SocketException) when (_lifetime.IsCancellationRequested)
            {
            }
        }

        private async Task HandleAsync(TcpClient client)
        {
            await using var stream = client.GetStream();
            var request = await ReadRequestAsync(stream);
            var path = request.Path;
            if (path == "/v1/convert/file/async")
            {
                request.Headers.TryGetValue(DoclingV1ApiClient.ApiKeyHeaderName, out var apiKey);
                SubmitApiKey = apiKey;
                RawSubmitBody = Encoding.Latin1.GetString(request.Body);
                ExtractFile(RawSubmitBody);
                if (_redirect is not null)
                {
                    await WriteResponseAsync(
                        stream,
                        307,
                        string.Empty,
                        new Dictionary<string, string> { ["Location"] = new Uri(_redirect, DoclingV1ApiClient.SubmitPath).AbsoluteUri });
                    return;
                }

                await WriteResponseAsync(stream, 200, "{\"task_id\":\"task-1\",\"task_status\":\"pending\"}");
                return;
            }

            if (path == "/v1/status/poll/task-1")
            {
                request.Headers.TryGetValue(DoclingV1ApiClient.ApiKeyHeaderName, out var apiKey);
                PollApiKey = apiKey;
                var count = Interlocked.Increment(ref _pollCount);
                await WriteResponseAsync(stream, 200, count == 1
                    ? "{\"task_id\":\"task-1\",\"task_status\":\"started\"}"
                    : "{\"task_id\":\"task-1\",\"task_status\":\"success\"}");
                return;
            }

            if (path == "/v1/result/task-1")
            {
                request.Headers.TryGetValue(DoclingV1ApiClient.ApiKeyHeaderName, out var apiKey);
                ResultApiKey = apiKey;
                await WriteResponseAsync(stream, 200, _result);
                return;
            }

            await WriteResponseAsync(stream, 404, string.Empty);
        }

        private async Task<RequestData> ReadRequestAsync(NetworkStream stream)
        {
            using var bytes = new MemoryStream();
            var buffer = new byte[4096];
            var headerEnd = -1;
            while (headerEnd < 0)
            {
                var read = await stream.ReadAsync(buffer, _lifetime.Token);
                if (read == 0)
                {
                    throw new EndOfStreamException();
                }

                bytes.Write(buffer, 0, read);
                headerEnd = FindHeaderEnd(bytes.GetBuffer().AsSpan(0, checked((int)bytes.Length)));
            }

            var received = bytes.ToArray();
            var headerText = Encoding.ASCII.GetString(received, 0, headerEnd);
            var lines = headerText.Split("\r\n", StringSplitOptions.None);
            var requestLine = lines[0].Split(' ', StringSplitOptions.RemoveEmptyEntries);
            var headers = lines.Skip(1)
                .Select(line => line.Split(':', 2))
                .Where(parts => parts.Length == 2)
                .ToDictionary(parts => parts[0], parts => parts[1].Trim(), StringComparer.OrdinalIgnoreCase);
            var contentLength = headers.TryGetValue("Content-Length", out var length)
                ? int.Parse(length, System.Globalization.CultureInfo.InvariantCulture)
                : 0;
            var bodyOffset = headerEnd + 4;
            while (received.Length - bodyOffset < contentLength)
            {
                var read = await stream.ReadAsync(buffer, _lifetime.Token);
                if (read == 0)
                {
                    throw new EndOfStreamException();
                }

                bytes.Write(buffer, 0, read);
                received = bytes.ToArray();
            }

            return new RequestData(requestLine[1], headers, received.AsSpan(bodyOffset, contentLength).ToArray());
        }

        private static int FindHeaderEnd(ReadOnlySpan<byte> value)
        {
            for (var index = 0; index <= value.Length - 4; index++)
            {
                if (value[index] == '\r' && value[index + 1] == '\n' &&
                    value[index + 2] == '\r' && value[index + 3] == '\n')
                {
                    return index;
                }
            }

            return -1;
        }

        private void ExtractFile(string multipart)
        {
            var marker = "name=files";
            var markerIndex = multipart.IndexOf(marker, StringComparison.Ordinal);
            if (markerIndex < 0)
            {
                return;
            }

            var filenamePrefix = "filename=";
            var filenameIndex = multipart.IndexOf(filenamePrefix, markerIndex, StringComparison.Ordinal);
            var filenameEnd = multipart.IndexOf("\r\n", filenameIndex, StringComparison.Ordinal);
            FileName = multipart[(filenameIndex + filenamePrefix.Length)..filenameEnd]
                .Split(';', 2)[0]
                .Trim('"');
            var headerEnd = multipart.IndexOf("\r\n\r\n", filenameEnd, StringComparison.Ordinal) + 4;
            var dataEnd = multipart.IndexOf("\r\n--", headerEnd, StringComparison.Ordinal);
            _uploadBytes.AddRange(Encoding.Latin1.GetBytes(multipart[headerEnd..dataEnd]));
        }

        private static async Task WriteResponseAsync(
            NetworkStream stream,
            int statusCode,
            string json,
            IReadOnlyDictionary<string, string>? headers = null)
        {
            var bytes = Encoding.UTF8.GetBytes(json);
            var reason = statusCode switch { 200 => "OK", 307 => "Temporary Redirect", 404 => "Not Found", _ => "Error" };
            var response = new StringBuilder()
                .Append("HTTP/1.1 ").Append(statusCode).Append(' ').Append(reason).Append("\r\n")
                .Append("Content-Type: application/json\r\n")
                .Append("Content-Length: ").Append(bytes.Length).Append("\r\n")
                .Append("Connection: close\r\n");
            if (headers is not null)
            {
                foreach (var header in headers)
                {
                    response.Append(header.Key).Append(": ").Append(header.Value).Append("\r\n");
                }
            }

            response.Append("\r\n");
            await stream.WriteAsync(Encoding.ASCII.GetBytes(response.ToString()));
            await stream.WriteAsync(bytes);
        }

        public async ValueTask DisposeAsync()
        {
            _lifetime.Cancel();
            _listener.Stop();
            try
            {
                await _runTask;
            }
            catch (ObjectDisposedException)
            {
            }

            _lifetime.Dispose();
        }

        private sealed record RequestData(
            string Path,
            IReadOnlyDictionary<string, string> Headers,
            byte[] Body);
    }
}

using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;

namespace Loregrove.Infrastructure.Docling;

internal sealed record DoclingProcessStartSpec(
    string FileName,
    string WorkingDirectory,
    IReadOnlyList<string> Arguments);

internal interface IDoclingCommandBuilder
{
    DoclingProcessStartSpec Build(
        DoclingPackLocation location,
        DoclingProcessingPackManifest manifest,
        int port);
}

internal sealed class DoclingCommandBuilder : IDoclingCommandBuilder
{
    internal const string LoopbackAddress = "127.0.0.1";

    public DoclingProcessStartSpec Build(
        DoclingPackLocation location,
        DoclingProcessingPackManifest manifest,
        int port)
    {
        if (port is < 1 or > 65535)
        {
            throw new ArgumentOutOfRangeException(nameof(port));
        }

        if (manifest.CommandContractVersion != FileSystemDoclingPackValidator.SupportedCommandContractVersion ||
            !FileSystemDoclingPackValidator.TryResolvePackFile(
                location.RootPath,
                manifest.EntryPoint,
                out var entryPoint))
        {
            throw new InvalidOperationException("The Docling Processing Pack launch contract is invalid.");
        }

        // These are Loregrove Processing Pack launcher v1 arguments, not raw docling-serve switches.
        // The pack launcher owns translation to the exact pinned docling-serve command interface.
        return new(
            entryPoint,
            Path.GetFullPath(location.RootPath),
            [
                "--host",
                LoopbackAddress,
                "--port",
                port.ToString(System.Globalization.CultureInfo.InvariantCulture),
                "--disable-ui",
                "--local-files-only",
            ]);
    }
}

internal interface ILoopbackPortAllocator
{
    int Allocate();
}

internal sealed class LoopbackPortAllocator : ILoopbackPortAllocator
{
    public int Allocate()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        try
        {
            listener.Start();
            return ((IPEndPoint)listener.LocalEndpoint).Port;
        }
        finally
        {
            listener.Stop();
        }
    }
}

internal interface IDoclingReadinessProbe
{
    Task<bool> IsReadyAsync(Uri endpoint, CancellationToken cancellationToken);
}

internal interface IDoclingShutdownSignal
{
    Task<bool> RequestShutdownAsync(Uri endpoint, CancellationToken cancellationToken);
}

internal sealed class HttpDoclingControlClient :
    IDoclingReadinessProbe,
    IDoclingShutdownSignal,
    IDisposable
{
    internal const string ReadinessPath = "health";
    internal const string ShutdownPath = "shutdown";

    private readonly HttpClient _httpClient;

    public HttpDoclingControlClient()
    {
        _httpClient = new HttpClient(new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            UseProxy = false,
        });
    }

    public async Task<bool> IsReadyAsync(Uri endpoint, CancellationToken cancellationToken)
    {
        try
        {
            using var response = await _httpClient.GetAsync(
                new Uri(endpoint, ReadinessPath),
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken);
            return response.IsSuccessStatusCode;
        }
        catch (HttpRequestException)
        {
            return false;
        }
    }

    public async Task<bool> RequestShutdownAsync(Uri endpoint, CancellationToken cancellationToken)
    {
        try
        {
            using var response = await _httpClient.PostAsync(
                new Uri(endpoint, ShutdownPath),
                content: null,
                cancellationToken);
            return response.IsSuccessStatusCode;
        }
        catch (HttpRequestException)
        {
            return false;
        }
    }

    public void Dispose() => _httpClient.Dispose();
}

internal sealed record ChildProcessDiagnostics(string StandardOutput, string StandardError);

internal interface IChildProcess : IAsyncDisposable
{
    int Id { get; }

    bool HasExited { get; }

    Task<int> ExitTask { get; }

    ChildProcessDiagnostics GetDiagnostics();

    void KillTree();
}

internal interface IChildProcessLauncher
{
    IChildProcess Start(DoclingProcessStartSpec startSpec);
}

internal sealed class SystemChildProcessLauncher : IChildProcessLauncher
{
    public IChildProcess Start(DoclingProcessStartSpec startSpec)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = startSpec.FileName,
            WorkingDirectory = startSpec.WorkingDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };

        foreach (var argument in startSpec.Arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        var process = new Process
        {
            StartInfo = startInfo,
            EnableRaisingEvents = true,
        };

        try
        {
            if (!process.Start())
            {
                throw new InvalidOperationException("The Docling child process did not start.");
            }

            return new SystemChildProcess(process);
        }
        catch
        {
            process.Dispose();
            throw;
        }
    }
}

internal sealed class SystemChildProcess : IChildProcess
{
    // Two UTF-16 buffers of this size retain approximately 128 KiB of character storage in total.
    internal const int DiagnosticCharactersPerStream = 32 * 1024;

    private readonly Process _process;
    private readonly BoundedTextBuffer _standardOutput = new(DiagnosticCharactersPerStream);
    private readonly BoundedTextBuffer _standardError = new(DiagnosticCharactersPerStream);
    private readonly CancellationTokenSource _outputCancellation = new();
    private readonly Task _outputPump;
    private readonly Task _errorPump;
    private int _disposed;

    public SystemChildProcess(Process process)
    {
        _process = process;
        _outputPump = DrainAsync(process.StandardOutput, _standardOutput, _outputCancellation.Token);
        _errorPump = DrainAsync(process.StandardError, _standardError, _outputCancellation.Token);
        ExitTask = WaitForExitAsync(process);
    }

    public int Id => _process.Id;

    public bool HasExited
    {
        get
        {
            try
            {
                return _process.HasExited;
            }
            catch (InvalidOperationException)
            {
                return true;
            }
        }
    }

    public Task<int> ExitTask { get; }

    public ChildProcessDiagnostics GetDiagnostics() =>
        new(_standardOutput.Snapshot(), _standardError.Snapshot());

    public void KillTree()
    {
        if (!HasExited)
        {
            _process.Kill(entireProcessTree: true);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _outputCancellation.Cancel();
        try
        {
            await Task.WhenAll(_outputPump, _errorPump).WaitAsync(TimeSpan.FromSeconds(1));
        }
        catch (OperationCanceledException)
        {
        }
        catch (TimeoutException)
        {
        }

        _outputCancellation.Dispose();
        _process.Dispose();
    }

    private static async Task<int> WaitForExitAsync(Process process)
    {
        await process.WaitForExitAsync();
        return process.ExitCode;
    }

    private static async Task DrainAsync(
        StreamReader reader,
        BoundedTextBuffer destination,
        CancellationToken cancellationToken)
    {
        var buffer = new char[4096];
        try
        {
            while (true)
            {
                var read = await reader.ReadAsync(buffer.AsMemory(), cancellationToken);
                if (read == 0)
                {
                    return;
                }

                destination.Append(buffer.AsSpan(0, read));
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (IOException)
        {
        }
    }
}

internal sealed class BoundedTextBuffer
{
    private readonly int _capacity;
    private readonly StringBuilder _buffer;
    private readonly object _sync = new();

    public BoundedTextBuffer(int capacity)
    {
        _capacity = capacity;
        _buffer = new StringBuilder(capacity);
    }

    public void Append(ReadOnlySpan<char> value)
    {
        lock (_sync)
        {
            if (value.Length >= _capacity)
            {
                _buffer.Clear();
                _buffer.Append(value[^_capacity..]);
                return;
            }

            var overflow = _buffer.Length + value.Length - _capacity;
            if (overflow > 0)
            {
                _buffer.Remove(0, overflow);
            }

            _buffer.Append(value);
        }
    }

    public string Snapshot()
    {
        lock (_sync)
        {
            return _buffer.ToString();
        }
    }
}

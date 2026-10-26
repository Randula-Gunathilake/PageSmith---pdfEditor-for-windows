using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.Principal;

namespace PageSmith.Core.Common;

/// <summary>
/// Keeps one PageSmith window per signed-in user: starting it again (opening a PDF from Explorer, say)
/// hands the files to the running window, which opens them in tabs, and the new process exits.
/// </summary>
public sealed class SingleInstance : IDisposable
{
    private readonly Mutex _mutex;
    private readonly string _key;
    private readonly CancellationTokenSource _cts = new();

    private SingleInstance(Mutex mutex, string key)
    {
        _mutex = mutex;
        _key = key;
    }

    /// <summary>Names the mutex and pipe; per user, since each user has their own PageSmith.</summary>
    internal static string DefaultKey { get; } = $"PageSmith-{WindowsIdentity.GetCurrent().User?.Value ?? Environment.UserName}";

    /// <summary>Claims the single instance; null when another PageSmith is already running.</summary>
    public static SingleInstance? TryClaim() => TryClaim(DefaultKey);

    internal static SingleInstance? TryClaim(string key)
    {
        var mutex = new Mutex(initiallyOwned: true, $@"Local\{key}", out var created);
        if (created) return new SingleInstance(mutex, key);
        mutex.Dispose();
        return null;
    }

    /// <summary>Sends files to the running PageSmith (none just brings its window forward); false if it didn't answer.</summary>
    public static bool Forward(IReadOnlyList<string> files) => Forward(DefaultKey, files);

    internal static bool Forward(string key, IReadOnlyList<string> files)
    {
        try
        {
            // The running window may take the foreground: it is answering the user's own action.
            AllowSetForegroundWindow(-1);
            using var client = new NamedPipeClientStream(".", key, PipeDirection.Out, PipeOptions.CurrentUserOnly);
            client.Connect(3000);
            using var writer = new StreamWriter(client, new UTF8Encoding(false));
            foreach (var file in files) writer.WriteLine(file);
            writer.Flush();
            return true;
        }
        catch (Exception ex) when (ex is TimeoutException or IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>
    /// Waits for later starts; <paramref name="open"/> is called on a background thread with each one's files.
    /// <paramref name="error"/> hears about anything that goes wrong while listening.
    /// </summary>
    public void Listen(Action<IReadOnlyList<string>> open, Action<Exception>? error = null) => _ = ListenAsync(open, error, _cts.Token);

    private async Task ListenAsync(Action<IReadOnlyList<string>> open, Action<Exception>? error, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await using var server = new NamedPipeServerStream(_key, PipeDirection.In, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await server.WaitForConnectionAsync(ct);
                using var reader = new StreamReader(server, Encoding.UTF8);
                var files = new List<string>();
                while (await reader.ReadLineAsync(ct) is { } line)
                    if (line.Length > 0) files.Add(line);
                open(files);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                error?.Invoke(ex);
                try { await Task.Delay(1000, ct); } catch (OperationCanceledException) { return; }
            }
        }
    }

    public void Dispose()
    {
        _cts.Cancel();
        _mutex.Dispose();
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AllowSetForegroundWindow(int processId);
}

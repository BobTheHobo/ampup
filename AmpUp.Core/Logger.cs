using System.IO;
using System.Reflection;

namespace AmpUp.Core;

public static class Logger
{
    public static readonly string LogPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "AmpUp", "ampup.log");
    private static readonly object _lock = new();

    // Persistent buffered writer — opened lazily on first log, flushed by a
    // ~1s timer and on process exit. FileShare.Read lets the user tail the
    // file while the app runs. If the writer ever fails (file locked, disk
    // error), logging degrades to a no-op instead of throwing into the
    // serial/N3/UI threads that call Log().
    private static StreamWriter? _writer;
    private static bool _writerFailed;
    private static System.Threading.Timer? _flushTimer;

    static Logger()
    {
        try
        {
            var dir = Path.GetDirectoryName(LogPath)!;
            Directory.CreateDirectory(dir);

            // Rotate: delete log if > 1MB
            if (File.Exists(LogPath) && new FileInfo(LogPath).Length > 1_048_576)
                File.Delete(LogPath);

            var version = (Assembly.GetEntryAssembly() ?? typeof(Logger).Assembly)
                .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
                ?.InformationalVersion?.Split('+')[0] ?? "0.0.0";
            var line = $"=== AmpUp {version} started {DateTime.Now:yyyy-MM-dd HH:mm:ss} ===";
            lock (_lock)
            {
                WriteLine(line);
            }
        }
        catch { /* ignore startup log failures */ }
    }

    /// <summary>Optional callback for UI log display.</summary>
    public static event Action<string>? OnLogMessage;

    public static void Log(string message)
    {
        var line = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {message}";
#if DEBUG
        Console.WriteLine(line);
#endif
        OnLogMessage?.Invoke(line);
        lock (_lock)
        {
            WriteLine(line);
        }
    }

    /// <summary>Appends a line to the buffered writer. Callers must hold <see cref="_lock"/>.</summary>
    private static void WriteLine(string line)
    {
        try
        {
            var writer = EnsureWriter();
            writer?.WriteLine(line);
        }
        catch
        {
            DisableWriter();
        }
    }

    /// <summary>Lazily opens the log writer. Callers must hold <see cref="_lock"/>.</summary>
    private static StreamWriter? EnsureWriter()
    {
        if (_writerFailed) return null;
        if (_writer != null) return _writer;

        try
        {
            var dir = Path.GetDirectoryName(LogPath)!;
            Directory.CreateDirectory(dir);

            var stream = new FileStream(LogPath, FileMode.Append, FileAccess.Write, FileShare.Read);
            _writer = new StreamWriter(stream) { AutoFlush = false };

            _flushTimer ??= new System.Threading.Timer(_ => FlushPending(), null, 1000, 1000);
            AppDomain.CurrentDomain.ProcessExit += OnProcessExit;
            return _writer;
        }
        catch
        {
            _writerFailed = true;
            return null;
        }
    }

    private static void FlushPending()
    {
        lock (_lock)
        {
            try { _writer?.Flush(); }
            catch { DisableWriter(); }
        }
    }

    private static void OnProcessExit(object? sender, EventArgs e)
    {
        lock (_lock)
        {
            try
            {
                _flushTimer?.Dispose();
                _flushTimer = null;
                _writer?.Flush();
                _writer?.Dispose();
            }
            catch { /* ignore shutdown log failures */ }
            _writer = null;
            _writerFailed = true; // no reopen during teardown
        }
    }

    /// <summary>Disables file logging after a write/flush failure. Callers must hold <see cref="_lock"/>.</summary>
    private static void DisableWriter()
    {
        try { _writer?.Dispose(); } catch { }
        _writer = null;
        _writerFailed = true;
    }
}

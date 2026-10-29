using System.Diagnostics;

namespace WindFlow.App;

/// <summary>One timestamped text file per launch under %LOCALAPPDATA%\WindFlow\logs, flushed per line so a
/// process that dies still leaves its last words on disk, mirrored to Debug output. Only events are logged
/// (commands, edits, sessions, failures), never frames.
/// ponytail: no rotation by size, no levels filter; a launch writes a few hundred lines at most.</summary>
public static class Log
{
    const int KeepFiles = 10;
    static readonly object Gate = new();
    static StreamWriter? writer;

    public static string Folder { get; } = Path.Combine(
        System.Environment.GetFolderPath(System.Environment.SpecialFolder.LocalApplicationData), "WindFlow", "logs");

    /// <summary>This launch's file, or "" when the folder could not be written.</summary>
    public static string FilePath { get; private set; } = "";

    /// <summary>Bumped on every Error/Fatal line; the status strip shows the path while this is recent.</summary>
    public static volatile int ErrorCount;
    public static DateTime LastErrorUtc = DateTime.MinValue;

    public static void Init()
    {
        try
        {
            Directory.CreateDirectory(Folder);
            var old = new DirectoryInfo(Folder).GetFiles("windflow-*.log")
                .OrderByDescending(f => f.Name, StringComparer.Ordinal).Skip(KeepFiles - 1);
            foreach (var f in old)
                try { f.Delete(); } catch (IOException) { /* another instance still has it open */ }
            FilePath = Path.Combine(Folder, $"windflow-{DateTime.Now:yyyyMMdd-HHmmss}.log");
            var stream = new FileStream(FilePath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
            writer = new StreamWriter(stream) { AutoFlush = true };
        }
        catch (Exception e)
        {
            FilePath = "";
            Debug.WriteLine("WindFlow log unavailable: " + e.Message);
        }
        Info($"WindFlow {typeof(Log).Assembly.GetName().Version} · {System.Environment.OSVersion} · " +
             $".NET {System.Environment.Version} · args: {string.Join(' ', System.Environment.GetCommandLineArgs().Skip(1))}");
    }

    public static void Info(string message) => Write("INFO ", message);

    public static void Warn(string message) => Write("WARN ", message);

    /// <summary>Full exception text (type, message, stack, inner exceptions) goes into the file.</summary>
    public static void Error(string message, Exception? e = null)
    {
        ErrorCount++;
        LastErrorUtc = DateTime.UtcNow;
        Write("ERROR", e == null ? message : message + System.Environment.NewLine + e);
    }

    public static void Fatal(string message, Exception? e)
    {
        ErrorCount++;
        LastErrorUtc = DateTime.UtcNow;
        Write("FATAL", e == null ? message : message + System.Environment.NewLine + e);
    }

    static void Write(string level, string message)
    {
        var thread = Thread.CurrentThread;
        string line = $"{DateTime.Now:HH:mm:ss.fff} {level} [{thread.Name ?? "#" + thread.ManagedThreadId}] {message}";
        Debug.WriteLine(line);
        lock (Gate)
        {
            try { writer?.WriteLine(line); } catch (Exception) { /* disk full or gone: Debug still has it */ }
        }
    }

    /// <summary>"12.3 ms" since a Stopwatch timestamp.</summary>
    public static string Ms(long since) =>
        $"{(Stopwatch.GetTimestamp() - since) * 1000.0 / Stopwatch.Frequency:0.0} ms";
}

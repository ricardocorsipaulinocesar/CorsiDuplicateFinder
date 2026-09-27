using System.Runtime.CompilerServices;
using System.Text.Json;

namespace CorsiDuplicate.Infrastructure.Logging;

public enum LogLevel
{
    Debug,
    Info,
    Warning,
    Error,
    Fatal
}

/// <summary>
/// Append-only JSON-lines logger (one JSON object per line) written to a "log" folder
/// next to the executable — <c>AppContext.BaseDirectory</c> is the published app's own
/// directory for a self-contained deployment, not the user's working directory. Every
/// call is best-effort: a failure to write a log line must never itself crash the app,
/// since these calls sit inside the same methods this logging exists to help debug.
/// </summary>
public static class AppLogger
{
    private static readonly object Sync = new();
    private static readonly string LogDirectory;
    private static readonly string LogFilePath;

    static AppLogger()
    {
        LogDirectory = Path.Combine(AppContext.BaseDirectory, "log");
        try
        {
            Directory.CreateDirectory(LogDirectory);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }

        LogFilePath = Path.Combine(LogDirectory, $"log-{DateTime.Now:yyyyMMdd}.json");
    }

    public static void Debug(string className, string method, string description, [CallerLineNumber] int line = 0) =>
        Write(LogLevel.Debug, className, method, line, description);

    public static void Info(string className, string method, string description, [CallerLineNumber] int line = 0) =>
        Write(LogLevel.Info, className, method, line, description);

    public static void Warning(string className, string method, string description, [CallerLineNumber] int line = 0) =>
        Write(LogLevel.Warning, className, method, line, description);

    public static void Error(string className, string method, string description, Exception? exception = null, [CallerLineNumber] int line = 0) =>
        Write(LogLevel.Error, className, method, ExceptionLine(exception, line), Describe(description, exception));

    public static void Fatal(string className, string method, string description, Exception? exception = null, [CallerLineNumber] int line = 0) =>
        Write(LogLevel.Fatal, className, method, ExceptionLine(exception, line), Describe(description, exception));

    /// <summary>Prefers the exception's own throw-site line (from its stack trace) over the caller's log-call line, when available.</summary>
    private static int ExceptionLine(Exception? exception, int fallbackLine)
    {
        if (exception is null)
        {
            return fallbackLine;
        }

        var frame = new System.Diagnostics.StackTrace(exception, true).GetFrame(0);
        var exceptionLine = frame?.GetFileLineNumber() ?? 0;
        return exceptionLine > 0 ? exceptionLine : fallbackLine;
    }

    private static string Describe(string description, Exception? exception) =>
        exception is null
            ? description
            : $"{description} | {exception.GetType().Name}: {exception.Message}{Environment.NewLine}{exception.StackTrace}";

    private static void Write(LogLevel level, string className, string method, int line, string description)
    {
        try
        {
            var entry = new LogEntry(DateTime.Now.ToString("o"), level.ToString(), className, method, line, description);
            var json = JsonSerializer.Serialize(entry);

            lock (Sync)
            {
                File.AppendAllText(LogFilePath, json + Environment.NewLine);
            }
        }
        catch (Exception)
        {
            // Logging must never be the reason the app fails.
        }
    }

    private sealed record LogEntry(
        string Timestamp,
        string Level,
        string Class,
        string Method,
        int Line,
        string Description);
}

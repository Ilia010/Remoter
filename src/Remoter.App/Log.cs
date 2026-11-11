using System.Text;

namespace Remoter.App;

/// <summary>Простой потокобезопасный файловый логгер без внешних зависимостей.</summary>
public static class Log
{
    private static readonly object Sync = new();
    private static string? _directory;
    private static string? _secret;
    private static int _cleanupDone;

    public static void Init(string directory, string? secret = null)
    {
        _directory = directory;
        _secret = secret;
        Directory.CreateDirectory(directory);
        Cleanup();
        Info("=== Remoter start ===");
    }

    public static void Info(string message) => Write("INFO", message);
    public static void Warn(string message) => Write("WARN", message);
    public static void Error(string message, Exception? ex = null) =>
        Write("ERROR", ex is null ? message : $"{message}{Environment.NewLine}{ex}");

    private static void Write(string level, string message)
    {
        if (_directory is null) return;

        var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{level}] {Redact(message)}";
        lock (Sync)
        {
            try
            {
                var file = Path.Combine(_directory, $"remoter-{DateTime.Now:yyyy-MM-dd}.log");
                File.AppendAllText(file, line + Environment.NewLine, Encoding.UTF8);
            }
            catch
            {
                // логгер не должен ронять приложение
            }
        }
    }

    private static string Redact(string message)
    {
        if (string.IsNullOrEmpty(_secret)) return message;
        return message.Replace(_secret, "***", StringComparison.Ordinal);
    }

    private static void Cleanup()
    {
        if (Interlocked.Exchange(ref _cleanupDone, 1) == 1 || _directory is null) return;
        try
        {
            foreach (var file in Directory.GetFiles(_directory, "remoter-*.log"))
            {
                if (File.GetLastWriteTimeUtc(file) < DateTime.UtcNow.AddDays(-14))
                    File.Delete(file);
            }
        }
        catch
        {
            // игнорируем
        }
    }
}

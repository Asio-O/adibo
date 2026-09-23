using System.Text;
using System.Text.Json;

namespace AdiboProxy.Logging;

/// <summary>
/// 极简 JSONL 文件日志：一行一条记录，方便直接 grep / 用脚本解析。
/// 手写 JSON 而不走序列化器，是为了 Native AOT 下零反射依赖。
/// </summary>
public sealed class FileLoggerProvider : ILoggerProvider
{
    private readonly string _directory;
    private readonly LogLevel _minimum;
    private readonly int _retainDays;
    private readonly object _gate = new();
    private DateTime _lastPrune = DateTime.MinValue;

    public FileLoggerProvider(string directory, LogLevel minimum, int retainDays = 7)
    {
        _directory = directory;
        _minimum = minimum;
        _retainDays = retainDays;
        Directory.CreateDirectory(directory);
    }

    public ILogger CreateLogger(string categoryName) => new FileLogger(this, categoryName);

    public void Dispose()
    {
    }

    internal bool IsEnabled(LogLevel level) => level >= _minimum && level != LogLevel.None;

    internal void Write(string category, LogLevel level, string message, Exception? exception)
    {
        if (!IsEnabled(level))
        {
            return;
        }

        var line = BuildLine(category, level, message, exception);

        lock (_gate)
        {
            try
            {
                PruneIfNeeded();
                var path = Path.Combine(_directory, $"rcouyi-{DateTime.Now:yyyyMMdd}.log");
                File.AppendAllText(path, line + Environment.NewLine, new UTF8Encoding(false));
            }
            catch (IOException)
            {
                // 日志写失败不能影响主流程。
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }

    private static string BuildLine(string category, LogLevel level, string message, Exception? exception)
    {
        var builder = new StringBuilder(message.Length + 160);
        builder.Append('{');
        Append(builder, "ts", DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm:ss.fff"));
        Append(builder, "level", level.ToString());
        Append(builder, "cat", category);
        Append(builder, "msg", message);
        if (exception is not null)
        {
            Append(builder, "ex", exception.GetType().Name + ": " + exception.Message);
            Append(builder, "stack", exception.StackTrace ?? string.Empty);
        }

        builder.Append('}');
        return builder.ToString();
    }

    private static void Append(StringBuilder builder, string name, string value)
    {
        if (builder.Length > 1)
        {
            builder.Append(',');
        }

        builder.Append('"').Append(name).Append("\":\"");
        builder.Append(JsonEncodedText.Encode(value));
        builder.Append('"');
    }

    private void PruneIfNeeded()
    {
        if ((DateTime.UtcNow - _lastPrune).TotalHours < 6)
        {
            return;
        }

        _lastPrune = DateTime.UtcNow;
        var cutoff = DateTime.Now.AddDays(-_retainDays);

        foreach (var file in Directory.EnumerateFiles(_directory, "rcouyi-*.log"))
        {
            try
            {
                if (File.GetLastWriteTime(file) < cutoff)
                {
                    File.Delete(file);
                }
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }

    private sealed class FileLogger(FileLoggerProvider provider, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => provider.IsEnabled(logLevel);

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel))
            {
                return;
            }

            provider.Write(category, logLevel, formatter(state, exception), exception);
        }
    }
}

using Microsoft.Extensions.Logging;
using System.Collections.Concurrent;
using System.Text;

internal sealed class RollingFileLoggerProvider(RobotAppOptions.FileLoggingSettings settings) : ILoggerProvider
{
    private readonly ConcurrentDictionary<string, RollingFileLogger> loggers = new(StringComparer.OrdinalIgnoreCase);
    private readonly LogLevel minimumLevel = ParseLevel(settings.MinimumLevel);
    private readonly string directoryPath = Path.GetFullPath(settings.Directory);
    private readonly string fileNamePrefix = string.IsNullOrWhiteSpace(settings.FileNamePrefix) ? "robot" : settings.FileNamePrefix.Trim();
    private readonly long maxFileBytes = Math.Max(32 * 1024L, settings.MaxFileSizeKb * 1024L);
    private readonly int retainedFileCount = Math.Max(1, settings.RetainedFileCount);
    private readonly object writeLock = new();
    private StreamWriter? writer;
    private string? activeFilePath;
    private bool disposed;

    public ILogger CreateLogger(string categoryName)
    {
        return loggers.GetOrAdd(categoryName, static (name, state) => new RollingFileLogger(name, state), this);
    }

    public void Dispose()
    {
        lock (writeLock)
        {
            if (disposed)
            {
                return;
            }

            disposed = true;
            writer?.Dispose();
            writer = null;
        }
    }

    private void WriteLine(LogLevel logLevel, string categoryName, EventId eventId, string message, Exception? exception)
    {
        if (disposed || logLevel < minimumLevel)
        {
            return;
        }

        var timestamp = DateTimeOffset.Now.ToString("O");
        var builder = new StringBuilder(256);
        builder.Append(timestamp)
            .Append(' ')
            .Append('[').Append(logLevel).Append(']')
            .Append(' ')
            .Append(categoryName);

        if (eventId.Id != 0 || !string.IsNullOrWhiteSpace(eventId.Name))
        {
            builder.Append(" (").Append(eventId.Id);
            if (!string.IsNullOrWhiteSpace(eventId.Name))
            {
                builder.Append(':').Append(eventId.Name);
            }
            builder.Append(')');
        }

        builder.Append(": ").Append(message);
        if (exception is not null)
        {
            builder.AppendLine()
                .Append(exception);
        }

        lock (writeLock)
        {
            if (disposed)
            {
                return;
            }

            EnsureWriter(builder.Length + Environment.NewLine.Length);
            writer!.WriteLine(builder.ToString());
            writer.Flush();
        }
    }

    private void EnsureWriter(int upcomingChars)
    {
        Directory.CreateDirectory(directoryPath);

        if (writer is null)
        {
            OpenNewWriter();
            return;
        }

        if (activeFilePath is null)
        {
            OpenNewWriter();
            return;
        }

        var projectedBytes = Encoding.UTF8.GetByteCount(new string('x', upcomingChars));
        var currentLength = 0L;
        try
        {
            currentLength = new FileInfo(activeFilePath).Length;
        }
        catch
        {
            OpenNewWriter();
            return;
        }

        if (currentLength + projectedBytes <= maxFileBytes)
        {
            return;
        }

        RotateFiles();
        OpenNewWriter();
    }

    private void OpenNewWriter()
    {
        writer?.Dispose();
        activeFilePath = Path.Combine(directoryPath, $"{fileNamePrefix}.log");
        writer = new StreamWriter(new FileStream(activeFilePath, FileMode.Append, FileAccess.Write, FileShare.Read))
        {
            AutoFlush = true
        };
    }

    private void RotateFiles()
    {
        writer?.Dispose();
        writer = null;

        for (var index = retainedFileCount - 1; index >= 1; index--)
        {
            var source = Path.Combine(directoryPath, $"{fileNamePrefix}.{index}.log");
            var destination = Path.Combine(directoryPath, $"{fileNamePrefix}.{index + 1}.log");
            if (!File.Exists(source))
            {
                continue;
            }

            if (File.Exists(destination))
            {
                File.Delete(destination);
            }

            File.Move(source, destination);
        }

        var active = Path.Combine(directoryPath, $"{fileNamePrefix}.log");
        var firstArchive = Path.Combine(directoryPath, $"{fileNamePrefix}.1.log");
        if (File.Exists(firstArchive))
        {
            File.Delete(firstArchive);
        }

        if (File.Exists(active))
        {
            File.Move(active, firstArchive);
        }

        var overflow = Path.Combine(directoryPath, $"{fileNamePrefix}.{retainedFileCount + 1}.log");
        if (File.Exists(overflow))
        {
            File.Delete(overflow);
        }
    }

    private static LogLevel ParseLevel(string? value)
    {
        return Enum.TryParse<LogLevel>(value, ignoreCase: true, out var parsed)
            ? parsed
            : LogLevel.Debug;
    }

    private sealed class RollingFileLogger(string categoryName, RollingFileLoggerProvider provider) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None;

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

            provider.WriteLine(logLevel, categoryName, eventId, formatter(state, exception), exception);
        }
    }
}

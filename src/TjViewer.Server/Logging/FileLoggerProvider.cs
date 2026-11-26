using Microsoft.Extensions.Logging;
using TjViewer.Shared.Logging;

namespace TjViewer.Server.Logging;

/// <summary>
/// 把 ASP.NET Core 的 ILogger 输出落盘到 <see cref="FileLog"/>（client-/server-yyyyMMdd.log）。
/// 与内置控制台日志并行工作；文件写入失败会自动降级并告警，不影响服务运行。
/// </summary>
public sealed class FileLoggerProvider : ILoggerProvider
{
    private readonly LogSeverity _minLevel;

    public FileLoggerProvider(string app, LogSeverity minLevel = LogSeverity.Debug)
    {
        _minLevel = minLevel;
        FileLog.Initialize(app, minLevel);
    }

    public ILogger CreateLogger(string categoryName) => new FileLogger(categoryName, _minLevel);

    public void Dispose() { }

    private sealed class FileLogger : ILogger
    {
        private readonly string _category;
        private readonly LogSeverity _minLevel;

        public FileLogger(string category, LogSeverity minLevel)
        {
            // 只保留类别短名，避免每行前缀过长
            var dot = category.LastIndexOf('.');
            _category = dot >= 0 && dot < category.Length - 1 ? category[(dot + 1)..] : category;
            _minLevel = minLevel;
        }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel)
            => logLevel != LogLevel.None && Map(logLevel) >= _minLevel;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel)) return;
            try
            {
                FileLog.Write(Map(logLevel), $"[{_category}] {formatter(state, exception)}", exception);
            }
            catch
            {
                // 日志系统自身失败不得影响请求处理
            }
        }

        private static LogSeverity Map(LogLevel level) => level switch
        {
            LogLevel.Trace or LogLevel.Debug => LogSeverity.Debug,
            LogLevel.Information => LogSeverity.Info,
            LogLevel.Warning => LogSeverity.Warn,
            _ => LogSeverity.Error,
        };
    }
}

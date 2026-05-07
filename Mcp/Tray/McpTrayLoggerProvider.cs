using Microsoft.Extensions.Logging;

namespace GraphData.Mcp.Tray;

internal sealed class McpTrayLoggerProvider(McpTrayLogSink sink) : ILoggerProvider
{
    private readonly McpTrayLogSink _sink = sink;

    public ILogger CreateLogger(string categoryName)
    {
        return new McpTrayLogger(_sink, categoryName);
    }

    public void Dispose()
    {
    }

    private sealed class McpTrayLogger(McpTrayLogSink sink, string category) : ILogger
    {
        private readonly McpTrayLogSink _sink = sink;
        private readonly string _category = category;

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull
        {
            return null;
        }

        public bool IsEnabled(LogLevel logLevel)
        {
            return logLevel != LogLevel.None;
        }

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

            _sink.WriteLog(logLevel, _category, eventId, formatter(state, exception), exception);
        }
    }
}

using System.Globalization;
using Microsoft.Extensions.Logging;

namespace GraphData.Mcp.Tray;

internal sealed class McpTrayLoggerProvider(McpTrayLogSink sink) : ILoggerProvider, ISupportExternalScope
{
    private readonly McpTrayLogSink _sink = sink;
    private IExternalScopeProvider _scopeProvider = new LoggerExternalScopeProvider();

    public ILogger CreateLogger(string categoryName)
    {
        return new McpTrayLogger(_sink, categoryName, () => _scopeProvider);
    }

    public void SetScopeProvider(IExternalScopeProvider scopeProvider)
    {
        _scopeProvider = scopeProvider;
    }

    public void Dispose()
    {
    }

    private sealed class McpTrayLogger(
        McpTrayLogSink sink,
        string category,
        Func<IExternalScopeProvider> scopeProvider) : ILogger
    {
        private readonly McpTrayLogSink _sink = sink;
        private readonly string _category = category;
        private readonly Func<IExternalScopeProvider> _scopeProvider = scopeProvider;

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull
        {
            return _scopeProvider().Push(state);
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

            var properties = McpLogProperties.From(state, _scopeProvider());
            var text = StripStructuredEndpointPrefix(formatter(state, exception), properties.EndpointName);
            _sink.WriteLog(
                logLevel,
                _category,
                eventId,
                text,
                exception,
                properties.EndpointName,
                properties.Server,
                properties.Client,
                properties.Fields);
        }

        private static string StripStructuredEndpointPrefix(string text, string? endpointName)
        {
            if (string.IsNullOrWhiteSpace(endpointName) || !text.StartsWith(endpointName, StringComparison.Ordinal))
            {
                return text;
            }

            var remainder = text[endpointName.Length..].TrimStart();
            if (remainder.StartsWith(",", StringComparison.Ordinal))
            {
                remainder = remainder[1..].TrimStart();
            }

            return string.IsNullOrWhiteSpace(remainder) ? text : remainder;
        }
    }

    private sealed class McpLogProperties
    {
        private const string OriginalFormatKey = "{OriginalFormat}";

        public string? Server { get; private set; }

        public string? Client { get; private set; }

        public string? EndpointName { get; private set; }

        public IReadOnlyDictionary<string, string?>? Fields { get; private set; }

        public static McpLogProperties From<TState>(TState state, IExternalScopeProvider scopeProvider)
        {
            var result = new McpLogProperties();
            var fields = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);

            scopeProvider.ForEachScope(static (scope, state) => AddStructuredValues(scope, state), fields);
            AddStructuredValues(state, fields);

            result.EndpointName = FindField(fields, "endpointname");
            result.Server = FindEndpoint(fields, "server");
            result.Client = FindEndpoint(fields, "client");
            result.Fields = fields.Count == 0 ? null : fields;
            return result;
        }

        private static void AddStructuredValues(object? state, IDictionary<string, string?> fields)
        {
            if (state is null)
            {
                return;
            }

            if (state is IEnumerable<KeyValuePair<string, object?>> values)
            {
                foreach (var (key, value) in values)
                {
                    if (string.IsNullOrWhiteSpace(key) || string.Equals(key, OriginalFormatKey, StringComparison.Ordinal))
                    {
                        continue;
                    }

                    fields[key] = FormatValue(value);
                }
            }
        }

        private static string? FindEndpoint(IReadOnlyDictionary<string, string?> fields, string endpointName)
        {
            foreach (var (key, value) in fields)
            {
                if (string.IsNullOrWhiteSpace(value))
                {
                    continue;
                }

                var normalizedKey = NormalizeKey(key);
                if (string.Equals(normalizedKey, endpointName, StringComparison.Ordinal)
                    || string.Equals(normalizedKey, $"mcp{endpointName}", StringComparison.Ordinal)
                    || string.Equals(normalizedKey, $"{endpointName}info", StringComparison.Ordinal)
                    || string.Equals(normalizedKey, $"{endpointName}name", StringComparison.Ordinal)
                    || string.Equals(normalizedKey, $"{endpointName}id", StringComparison.Ordinal))
                {
                    return value;
                }
            }

            return null;
        }

        private static string? FindField(IReadOnlyDictionary<string, string?> fields, string normalizedName)
        {
            foreach (var (key, value) in fields)
            {
                if (!string.IsNullOrWhiteSpace(value) && string.Equals(NormalizeKey(key), normalizedName, StringComparison.Ordinal))
                {
                    return value;
                }
            }

            return null;
        }

        private static string NormalizeKey(string key)
        {
            Span<char> buffer = stackalloc char[key.Length];
            var length = 0;
            foreach (var character in key)
            {
                if (char.IsLetterOrDigit(character))
                {
                    buffer[length++] = char.ToLowerInvariant(character);
                }
            }

            return new string(buffer[..length]);
        }

        private static string? FormatValue(object? value)
        {
            return value switch
            {
                null => null,
                string text => text,
                IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
                _ => value.ToString()
            };
        }
    }
}

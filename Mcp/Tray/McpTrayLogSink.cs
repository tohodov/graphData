using System.Threading.Channels;
using Microsoft.Extensions.Logging;

namespace GraphData.Mcp.Tray;

internal sealed class McpTrayLogSink
{
    private readonly Channel<McpTrayMessage> _channel = Channel.CreateBounded<McpTrayMessage>(
        new BoundedChannelOptions(1000)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = false
        });

    public ChannelReader<McpTrayMessage> Reader => _channel.Reader;

    public void WriteLog(
        LogLevel level,
        string category,
        EventId eventId,
        string text,
        Exception? exception)
    {
        if (level == LogLevel.None)
        {
            return;
        }

        _channel.Writer.TryWrite(new McpTrayMessage
        {
            MessageType = "log",
            Timestamp = DateTimeOffset.UtcNow,
            Level = level,
            Category = category,
            EventId = eventId.Id,
            Text = text,
            Exception = exception?.ToString()
        });
    }
}

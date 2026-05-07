using System.IO.Pipes;
using System.Text;
using System.Text.Json;

namespace GraphData.McpTray;

internal sealed class NamedPipeLogServer(string pipeName, Action<McpTrayMessage> onMessage) : IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly string _pipeName = pipeName;
    private readonly Action<McpTrayMessage> _onMessage = onMessage;
    private readonly CancellationTokenSource _stopping = new();
    private readonly List<Task> _connectionTasks = [];
    private Task? _acceptTask;

    public void Start()
    {
        _acceptTask = Task.Run(AcceptLoopAsync);
    }

    public void Dispose()
    {
        _stopping.Cancel();
        try
        {
            _acceptTask?.Wait(TimeSpan.FromSeconds(2));
            Task.WaitAll(_connectionTasks.ToArray(), TimeSpan.FromSeconds(2));
        }
        catch
        {
        }

        _stopping.Dispose();
    }

    private async Task AcceptLoopAsync()
    {
        while (!_stopping.IsCancellationRequested)
        {
            var pipe = new NamedPipeServerStream(
                _pipeName,
                PipeDirection.In,
                NamedPipeServerStream.MaxAllowedServerInstances,
                PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous);

            try
            {
                await pipe.WaitForConnectionAsync(_stopping.Token);
                var task = Task.Run(() => ReadClientAsync(pipe, _stopping.Token));
                lock (_connectionTasks)
                {
                    _connectionTasks.Add(task);
                }
            }
            catch (OperationCanceledException)
            {
                await pipe.DisposeAsync();
                break;
            }
            catch
            {
                await pipe.DisposeAsync();
                await Task.Delay(250);
            }
        }
    }

    private async Task ReadClientAsync(NamedPipeServerStream pipe, CancellationToken cancellationToken)
    {
        await using (pipe)
        using (var reader = new StreamReader(pipe, Encoding.UTF8, detectEncodingFromByteOrderMarks: false))
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var line = await reader.ReadLineAsync(cancellationToken);
                if (line is null)
                {
                    break;
                }

                var message = TryDeserialize(line);
                if (message is not null)
                {
                    _onMessage(message);
                }
            }
        }
    }

    private static McpTrayMessage? TryDeserialize(string line)
    {
        try
        {
            return JsonSerializer.Deserialize<McpTrayMessage>(line, JsonOptions);
        }
        catch
        {
            return null;
        }
    }
}

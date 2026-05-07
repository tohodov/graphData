namespace GraphData.McpTray;

internal sealed class CommandLineOptions
{
    public string? StatusDirectory { get; private init; }

    public static CommandLineOptions Parse(string[] args)
    {
        for (var index = 0; index < args.Length; index++)
        {
            if (!string.Equals(args[index], "--status-dir", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (index + 1 < args.Length)
            {
                return new CommandLineOptions { StatusDirectory = args[index + 1] };
            }
        }

        return new CommandLineOptions();
    }
}

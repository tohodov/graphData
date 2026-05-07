namespace GraphData.McpTray;

internal sealed class CommandLineOptions
{
    public string PipeName { get; private init; } = "GraphDataMcpTray";

    public static CommandLineOptions Parse(string[] args)
    {
        for (var index = 0; index < args.Length; index++)
        {
            if (!string.Equals(args[index], "--pipe-name", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (index + 1 < args.Length)
            {
                return new CommandLineOptions { PipeName = args[index + 1] };
            }
        }

        return new CommandLineOptions();
    }
}

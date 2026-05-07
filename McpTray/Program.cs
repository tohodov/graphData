using System.Security.Cryptography;
using System.Text;
using GraphData.McpTray;

static class Program
{
    [STAThread]
    static void Main(string[] args)
    {
        var pipeName = CommandLineOptions.Parse(args).PipeName;

        using var mutex = new Mutex(
            initiallyOwned: true,
            name: $"Local\\GraphDataMcpTray-{HashPipeName(pipeName)}",
            createdNew: out var createdNew);

        if (!createdNew)
        {
            return;
        }

        ApplicationConfiguration.Initialize();
        Application.Run(new TrayApplicationContext(pipeName));
    }

    private static string HashPipeName(string pipeName)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(pipeName.ToUpperInvariant()));
        return Convert.ToHexString(bytes, 0, 8);
    }
}

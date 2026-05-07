using System.Security.Cryptography;
using System.Text;
using GraphData.McpTray;

static class Program
{
    [STAThread]
    static void Main(string[] args)
    {
        var statusDirectory = CommandLineOptions.Parse(args).StatusDirectory
            ?? Path.Combine(AppContext.BaseDirectory, "status");
        statusDirectory = Path.GetFullPath(statusDirectory);

        Directory.CreateDirectory(statusDirectory);

        using var mutex = new Mutex(
            initiallyOwned: true,
            name: $"Local\\GraphDataMcpTray-{HashStatusDirectory(statusDirectory)}",
            createdNew: out var createdNew);

        if (!createdNew)
        {
            return;
        }

        ApplicationConfiguration.Initialize();
        Application.Run(new TrayApplicationContext(statusDirectory));
    }

    private static string HashStatusDirectory(string statusDirectory)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(statusDirectory.ToUpperInvariant()));
        return Convert.ToHexString(bytes, 0, 8);
    }
}

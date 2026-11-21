using System.Text;
using DiffRapid.Hosting;
using ModelContextProtocol.Server;

namespace DiffRapid.Mcp;

internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        using var cancellation = new ConsoleCancellation();
        try
        {
            var settings = ServerSettings.Parse(args, Environment.CurrentDirectory);
            if (settings.UsesWorkingDirectory)
            {
                // The kind of root only: the path itself stays out of logs.
                await Console.Error.WriteLineAsync("diffrapid-mcp: no --root given; files are limited to the working directory.");
            }

            var options = ComparisonServer.CreateOptions(settings);
            await using var server = McpServer.Create(new StdioServerTransport(options), options);
            await server.RunAsync(cancellation.Token);
            return 0;
        }
        catch (OperationCanceledException) when (cancellation.Token.IsCancellationRequested)
        {
            return 0;
        }
        catch (Exception exception)
        {
            await Console.Error.WriteLineAsync(exception.Message);
            return 1;
        }
    }
}

using System.Text;
using ModelContextProtocol.Server;

namespace DiffRapid.Mcp;

internal static class Program
{
    private static async Task<int> Main()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        using var cancellation = new CancellationTokenSource();
        Console.CancelKeyPress += (_, arguments) =>
        {
            arguments.Cancel = true;
            cancellation.Cancel();
        };

        try
        {
            var options = ComparisonServer.CreateOptions();
            await using var server = McpServer.Create(new StdioServerTransport(options), options);
            await server.RunAsync(cancellation.Token);
            return 0;
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
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

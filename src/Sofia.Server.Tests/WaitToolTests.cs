using Sofia.Server.Context;
using Sofia.Server.Tools;

namespace Sofia.Server.Tests;

public class WaitToolTests
{
    private static ToolContext Context() => new()
    {
        Session = null!, Workspace = null!, Cwd = "", Message = null!, Call = null!,
        Config = null!, Changes = null!, Host = null!,
    };

    [Fact]
    public async Task WaitsAndReports()
    {
        var tool = new WaitTool();
        var result = await tool.ExecuteAsync(Context(), Args.Parse("""{"seconds":0}"""), CancellationToken.None);
        Assert.False(result.IsError);
        Assert.Contains("Nothing to wait", result.Output);
    }

    [Fact]
    public async Task RejectsNegative()
    {
        var tool = new WaitTool();
        var result = await tool.ExecuteAsync(Context(), Args.Parse("""{"seconds":-1}"""), CancellationToken.None);
        Assert.True(result.IsError);
    }

    [Fact]
    public async Task Cancels()
    {
        var tool = new WaitTool();
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var result = await tool.ExecuteAsync(Context(), Args.Parse("""{"seconds":10}"""), cts.Token);
        Assert.True(result.IsError);
        Assert.Contains("cancelled", result.Output, StringComparison.OrdinalIgnoreCase);
    }
}
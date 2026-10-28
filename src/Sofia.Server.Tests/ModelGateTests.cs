using Sofia.Server.Agent;
using Xunit;

namespace Sofia.Server.Tests;

public class ModelGateTests
{
    [Fact]
    public async Task SubagentsGoFirstAndOwnerKeepsTheModel()
    {
        var gate = new ModelGate();
        await gate.AcquireAsync("m", "A", false, default);
        await gate.AcquireAsync("m", "A", true, default); // re-entrant for the owner
        var b = gate.AcquireAsync("m", "B", false, default);
        var c = gate.AcquireAsync("m", "C", true, default);
        Assert.False(b.IsCompleted);
        gate.Release("A");
        await c.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.False(b.IsCompleted);
        gate.Release("C");
        await b.WaitAsync(TimeSpan.FromSeconds(2));
    }
}

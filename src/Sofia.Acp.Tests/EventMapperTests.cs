using System.Text.Json;
using System.Text.Json.Nodes;
using Sofia.Acp;

public class EventMapperTests
{
    [Theory]
    [InlineData("read", "read")]
    [InlineData("write", "edit")]
    [InlineData("grep", "search")]
    [InlineData("bash", "execute")]
    [InlineData("web_fetch", "fetch")]
    [InlineData("foo", "other")]
    public void ToolKind_Maps(string tool, string expected)
    {
        Assert.Equal(expected, EventMapper.ToolKind(tool));
    }

    [Fact]
    public void AgentMessageChunk_HasType()
    {
        var o = EventMapper.AgentMessageChunk("hi");
        Assert.Equal("agent_message_chunk", o["sessionUpdate"]!.GetValue<string>());
    }

    [Fact]
    public void Map_MessageDelta_Text()
    {
        var data = JsonSerializer.SerializeToElement(new { messageId = "m", field = "text", delta = "x" });
        var o = EventMapper.Map("message.delta", data);
        Assert.NotNull(o);
        Assert.Equal("agent_message_chunk", o!["sessionUpdate"]!.GetValue<string>());
    }

    [Fact]
    public void Map_TasksUpdated_Plan()
    {
        var data = JsonSerializer.SerializeToElement(new { sessionId = "s", tasks = new[] { new { id = "1", title = "do thing", status = "pending" } } });
        var o = EventMapper.Map("tasks.updated", data);
        Assert.NotNull(o);
        Assert.Equal("plan", o!["sessionUpdate"]!.GetValue<string>());
    }

    [Fact]
    public void DiffFromArgs_BuildsDiff()
    {
        var args = JsonNode.Parse("""{"path":"a.txt","oldText":"a","newText":"b"}""") as JsonObject;
        var d = EventMapper.DiffFromArgs(args);
        Assert.Equal("a.txt", d!["path"]!.GetValue<string>());
    }
}
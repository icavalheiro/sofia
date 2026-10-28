using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Sofia.Server.Browser;
using Sofia.Server.Context;
using Sofia.Server.Data;
using Sofia.Server.Domain;
using Sofia.Server.Infrastructure;
using Sofia.Server.Mcp;
using Sofia.Server.Tools;

namespace Sofia.Server.Agent;

/// <summary>Builds the tool set for an agent in a workspace.</summary>
public sealed class ToolRegistry(
    SofiaDb db, SofiaOptions options, McpManager mcp, SkillService skills, CommandService commands, AgentCatalog agents,
    IHttpClientFactory httpFactory, IServer server, BrowserService browser, KnowledgeService knowledge, ProcessManager processes)
{
    private static readonly HashSet<string> FolderOnly = ["read", "write", "edit", Agents.ShellToolToken, "grep", "find", "ls", "task", "task:explore"];

    public List<ITool> For(AgentDefinition agent, Workspace workspace)
    {
        var shell = ProcessRunner.ResolveShell(db.GetConfig().Shell);
        var dir = workspace.IsFolder ? workspace.Path : null;
        var tools = new List<ITool>();
        foreach (var name in agent.Tools)
        {
            if (!workspace.IsFolder && FolderOnly.Contains(name)) continue;
            ITool? tool = name switch
            {
                "read" => new ReadTool(SaveImage),
                "write" => new WriteTool(),
                "edit" => new EditTool(),
                Agents.ShellToolToken => new ShellTool(shell),
                "grep" => new GrepTool(),
                "find" => new FindTool(),
                "ls" => new LsTool(),
                "web_fetch" => new WebFetchTool(httpFactory),
                "web_search" => new WebSearchTool(httpFactory),
                "browser" => new BrowserTool(browser, SaveImage),
                "processes" => new ProcessesTool(processes, shell),
                "question" => new QuestionTool(),
                "task" => TaskFor(agents.Delegable(dir)),
                "task:explore" => TaskFor(agents.Delegable(dir, readOnlyOnly: true)),
                "skill" => new SkillTool(skills),
                "notes" => new NotesTool(knowledge),
                "memory" => new MemoryTool(db),
                "sofia_info" => new SofiaInfoTool(options, db, mcp, skills, commands, agents, BaseUrl),
                _ => null,
            };
            if (tool is not null && tools.All(t => t.Name != tool.Name)) tools.Add(tool);
        }
        if (agent.IncludeMcp) tools.AddRange(mcp.Tools());
        return tools;
    }

    private static TaskTool? TaskFor(List<AgentDefinition> targets) => targets.Count > 0 ? new TaskTool(targets) : null;

    /// <summary>Stores tool-made media (image, audio or video) like an uploaded attachment, so it is served from /api/attachments/{id}.</summary>
    private Attachment SaveImage(byte[] bytes, string mime, string name)
    {
        var id = Ids.New("att");
        var path = Path.Combine(options.AttachmentsDir, id + Path.GetExtension(name));
        File.WriteAllBytes(path, bytes);
        var attachment = new Attachment
        {
            Id = id,
            Name = name,
            Mime = mime,
            Kind = mime.StartsWith("audio/") ? "audio" : mime.StartsWith("video/") ? "video" : "image",
            Bytes = bytes.Length,
            Size = Text.HumanSize(bytes.Length),
            Url = $"/api/attachments/{id}",
            StoragePath = path,
        };
        db.Attachments.Insert(attachment);
        return attachment;
    }

    /// <summary>Where this server listens, as reachable from the same machine.</summary>
    private string BaseUrl()
    {
        var address = server.Features.Get<IServerAddressesFeature>()?.Addresses.FirstOrDefault();
        if (string.IsNullOrWhiteSpace(address)) return "http://127.0.0.1:5199";
        return address.Replace("0.0.0.0", "127.0.0.1").Replace("[::]", "127.0.0.1").Replace("//+", "//127.0.0.1").Replace("//*", "//127.0.0.1").TrimEnd('/');
    }
}

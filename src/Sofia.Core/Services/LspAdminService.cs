using Sofia.Core.Lsp;

namespace Sofia.Core.Services;

/// <summary>Language server administration: list with status, restart and install.</summary>
public sealed class LspAdminService(LspManager lsp)
{
    public object List() => lsp.ListViews();

    public async Task<object?> RestartAsync(string id)
    {
        if (LspServerCatalog.Find(id) is null) throw new NotFoundException($"Language server '{id}' not found.");
        await lsp.RestartAsync(id);
        return lsp.View(id);
    }

    public async Task<object> InstallAsync(string id, CancellationToken ct)
    {
        if (LspServerCatalog.Find(id) is null) throw new NotFoundException($"Language server '{id}' not found.");
        var (ok, output) = await lsp.InstallAsync(id, ct);
        return new { ok, output, status = lsp.View(id)?.Status };
    }
}

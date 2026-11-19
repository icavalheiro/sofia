// One-shot edit of src/Sofia.UI/src/i18n/index.ts for spec 035:
// drops the live-browser panel keys and adds the "default browser" settings strings to all six locales.
// Usage: dotnet run .sofia/scripts/i18n-browser-keys.cs
using System.Text;

var path = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../src/Sofia.UI/src/i18n/index.ts"));
if (!File.Exists(path)) path = Path.GetFullPath("src/Sofia.UI/src/i18n/index.ts");
var text = File.ReadAllText(path);
var newline = text.Contains("\r\n") ? "\r\n" : "\n";
var lines = text.Replace("\r\n", "\n").Split('\n').ToList();
if (text.Contains("'settings.defaultBrowser'", StringComparison.Ordinal))
{
    Console.WriteLine("The browser settings strings are already present; nothing to do.");
    return;
}

// 1. Remove the streamed-panel strings (header.browser + browser.*).
var removed = lines.RemoveAll(line =>
{
    var trimmed = line.TrimStart();
    return trimmed.StartsWith("'browser.", StringComparison.Ordinal) || trimmed.StartsWith("'header.browser'", StringComparison.Ordinal);
});

// 2. Insert the new settings strings after 'settings.reset' in each locale, in file order.
string[] brands =
[
    "  'settings.browser.chrome': 'Google Chrome',",
    "  'settings.browser.edge': 'Microsoft Edge',",
    "  'settings.browser.brave': 'Brave',",
    "  'settings.browser.vivaldi': 'Vivaldi',",
    "  'settings.browser.opera': 'Opera',",
    "  'settings.browser.chromium': 'Chromium',",
    "  'settings.browser.firefox': 'Firefox',",
];

string[][] blocks =
[
    [
        "  'settings.defaultBrowser': 'Default browser',",
        "  'settings.defaultBrowserSub':",
        "    'Browser the agent opens in a window on this machine. Chrome-family browsers are started by Sofia and driven over CDP; Firefox is experimental and cannot be re-attached after a restart.',",
        "  'settings.browser.auto': 'Automatic (first installed)',",
        .. brands,
    ],
    [
        "  'settings.defaultBrowser': 'Navegador padrão',",
        "  'settings.defaultBrowserSub':",
        "    'Navegador que o agente abre numa janela desta máquina. Navegadores da família Chrome são iniciados pela Sofia e controlados por CDP; o Firefox é experimental e não pode ser reconectado depois de reiniciar o servidor.',",
        "  'settings.browser.auto': 'Automático (primeiro instalado)',",
        .. brands,
    ],
    [
        "  'settings.defaultBrowser': 'Navegador predeterminado',",
        "  'settings.defaultBrowserSub':",
        "    'Navegador que abre el agente en una ventana de esta máquina. Los navegadores de la familia Chrome los inicia Sofia y se controlan por CDP; Firefox es experimental y no puede reconectarse tras reiniciar el servidor.',",
        "  'settings.browser.auto': 'Automático (primero instalado)',",
        .. brands,
    ],
    [
        "  'settings.defaultBrowser': 'Navigateur par défaut',",
        "  'settings.defaultBrowserSub':",
        "    'Navigateur que l’agent ouvre dans une fenêtre sur cette machine. Les navigateurs de la famille Chrome sont lancés par Sofia et pilotés via CDP ; Firefox est expérimental et ne peut pas être rattaché après un redémarrage.',",
        "  'settings.browser.auto': 'Automatique (premier installé)',",
        .. brands,
    ],
    [
        "  'settings.defaultBrowser': '默认浏览器',",
        "  'settings.defaultBrowserSub':",
        "    '智能体在本机窗口中打开的浏览器。Chrome 系列由 Sofia 启动并通过 CDP 控制；Firefox 为实验性支持，重启后无法重新连接。',",
        "  'settings.browser.auto': '自动（第一个已安装的）',",
        .. brands,
    ],
    [
        "  'settings.defaultBrowser': '既定のブラウザー',",
        "  'settings.defaultBrowserSub':",
        "    'エージェントがこのマシンのウィンドウで開くブラウザー。Chrome 系は Sofia が起動して CDP で操作します。Firefox は試験的で、再起動後に再接続できません。',",
        "  'settings.browser.auto': '自動（最初に見つかったもの）',",
        .. brands,
    ],
];

var locale = 0;
for (var i = 0; i < lines.Count && locale < blocks.Length; i++)
{
    if (!lines[i].TrimStart().StartsWith("'settings.reset'", StringComparison.Ordinal)) continue;
    lines.InsertRange(i + 1, blocks[locale]);
    i += blocks[locale].Length;
    locale++;
}

var output = string.Join("\n", lines);
if (newline == "\r\n") output = output.Replace("\n", "\r\n");
// Antivirus/indexers on Windows briefly lock the file: write with retries instead of failing the whole run.
for (var attempt = 1; ; attempt++)
{
    try
    {
        File.WriteAllText(path, output, new UTF8Encoding(false));
        break;
    }
    catch (IOException) when (attempt < 8)
    {
        Thread.Sleep(300);
    }
}
Console.WriteLine($"Removed {removed} panel keys and added the browser settings strings to {locale} locales.");

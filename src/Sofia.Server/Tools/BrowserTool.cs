using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.Playwright;
using Sofia.Server.Browser;
using Sofia.Server.Domain;
using Sofia.Server.Infrastructure;

namespace Sofia.Server.Tools;

/// <summary>
/// Drives the conversation's headless browser. The page is read as a text snapshot whose interactive elements carry
/// short refs (e1, e2…); the user watches the same page live in the UI and can take over between calls.
/// </summary>
public sealed partial class BrowserTool(BrowserService browser, Func<byte[], string, string, Attachment>? saveImage = null) : ITool
{
    private const int MaxShotHeight = 6000;

    private static readonly string[] Actions =
    [
        "navigate", "snapshot", "screenshot", "click", "hover", "type", "select", "press", "scroll", "wait",
        "back", "forward", "reload", "evaluate", "tabs", "new_tab", "switch_tab", "close_tab", "close",
    ];

    public string Name => "browser";

    public string Description =>
        "Control a real headless web browser that the user can watch live in the Sofia UI. Open pages, read them as a text snapshot " +
        "where interactive elements have refs (e1, e2…), then click, type, select, press keys and scroll. " +
        "Typical loop: navigate, read the snapshot, act on a ref, read the new snapshot. Refs are renumbered by every snapshot, so use refs from the latest one. " +
        "Use it for JavaScript-heavy sites, forms, logins (the user can take over in the UI) and checking web apps you build; prefer web_fetch/web_search for plain reading. " +
        "Use screenshot to actually look at a page (layout, charts, images, visual bugs); it only helps if you can see images, otherwise rely on snapshot.";

    public ToolKind Kind => ToolKind.Browser;

    public JsonObject Parameters => Schema.Object(
        ("action", Schema.Enum("What to do. navigate (url) · snapshot · screenshot (ref?, full_page?) · click (ref) · hover (ref) · type (ref?, text, submit?) · select (ref, value) · press (key, ref?) · " +
            "scroll (direction up|down|top|bottom, amount?, ref?) · wait (seconds? or text?) · back · forward · reload · evaluate (script) · tabs · new_tab (url?) · switch_tab (tab) · close_tab (tab?) · close.", Actions), true),
        ("url", Schema.String("Address for navigate/new_tab. Bare hosts get https://; plain text becomes a web search."), false),
        ("ref", Schema.String("Element ref from the latest snapshot, e.g. e12."), false),
        ("text", Schema.String("Text to type (type) or to wait for (wait)."), false),
        ("value", Schema.String("Option value or label to choose (select)."), false),
        ("key", Schema.String("Key or combo for press, e.g. Enter, Escape, Tab, ArrowDown, Control+a."), false),
        ("submit", Schema.Boolean("Press Enter after typing (type)."), false),
        ("full_page", Schema.Boolean("Capture the whole scrollable page instead of the visible viewport (screenshot; capped at 6000px tall)."), false),
        ("direction", Schema.Enum("Scroll direction.", "up", "down", "top", "bottom"), false),
        ("amount", Schema.Integer("Pixels to scroll (default: most of a screen)."), false),
        ("seconds", Schema.Integer("Seconds to wait (max 30)."), false),
        ("script", Schema.String("JavaScript expression or function to evaluate in the page; the result is returned as JSON."), false),
        ("tab", Schema.Integer("Tab number from the tabs action (0-based)."), false));

    public string Summarize(JsonObject args)
    {
        var action = Args.Optional(args, "action") ?? "";
        var detail = Args.Optional(args, "url") ?? Args.Optional(args, "ref") ?? Args.Optional(args, "key") ?? Args.Optional(args, "text") ?? "";
        return Text.FirstLine($"{action} {detail}".Trim(), 120);
    }

    public async Task<ToolResult> ExecuteAsync(ToolContext ctx, JsonObject args, CancellationToken ct)
    {
        var action = Args.Required(args, "action").Trim().ToLowerInvariant();
        if (!Actions.Contains(action)) return ToolResult.Fail($"Unknown action '{action}'. Actions: {string.Join(", ", Actions)}.");
        var rootId = ctx.Session.ParentId ?? ctx.Session.Id;
        if (action == "close")
        {
            await browser.CloseAsync(rootId);
            return ToolResult.Ok("Browser closed.");
        }

        BrowserSession session;
        try
        {
            session = await browser.GetAsync(rootId, ctx.Workspace.Id, ct);
        }
        catch (InvalidOperationException ex)
        {
            return ToolResult.Fail(ex.Message);
        }

        using var scope = await session.BeginAgentAsync(ct);
        try
        {
            if (action == "screenshot") return await ScreenshotAsync(session, args);
            return ToolResult.Ok(await RunAsync(session, action, args, ctx.Config.MaxToolOutput, ct));
        }
        catch (PlaywrightException ex) when (ex.Message.Contains("Timeout", StringComparison.OrdinalIgnoreCase))
        {
            return ToolResult.Fail($"{Text.FirstLine(ex.Message, 300)} The element may be hidden or covered; take a new snapshot and try again.");
        }
        catch (PlaywrightException ex)
        {
            return ToolResult.Fail(Text.FirstLine(ex.Message, 400));
        }
        catch (ArgumentException ex)
        {
            return ToolResult.Fail(ex.Message);
        }
    }

    private async Task<string> RunAsync(BrowserSession session, string action, JsonObject args, int maxChars, CancellationToken ct)
    {
        var page = session.Page;
        string done;
        switch (action)
        {
            case "navigate":
                await session.GotoAsync(Args.Required(args, "url"));
                done = "Navigated.";
                break;
            case "snapshot":
                done = "";
                break;
            case "click":
            {
                var target = await TargetAsync(session, args, "click");
                await target.ClickAsync();
                done = $"Clicked {Args.Required(args, "ref")}.";
                break;
            }
            case "hover":
            {
                var target = await TargetAsync(session, args, "hover");
                await target.HoverAsync();
                done = $"Hovered {Args.Required(args, "ref")}.";
                break;
            }
            case "type":
                done = await TypeAsync(session, args);
                break;
            case "select":
            {
                var target = await TargetAsync(session, args, "select");
                await target.SelectOptionAsync(Args.Required(args, "value"));
                done = $"Selected '{Args.Required(args, "value")}' in {Args.Required(args, "ref")}.";
                break;
            }
            case "press":
            {
                if (Args.Optional(args, "ref") is { Length: > 0 }) await (await TargetAsync(session, args, "press")).FocusAsync();
                var key = Args.Required(args, "key");
                await BrowserSession.PressKeyAsync(page, key);
                done = $"Pressed {key}.";
                break;
            }
            case "scroll":
                done = await ScrollAsync(session, args);
                break;
            case "wait":
                done = await WaitAsync(page, args, ct);
                break;
            case "back":
                await page.GoBackAsync();
                done = "Went back.";
                break;
            case "forward":
                await page.GoForwardAsync();
                done = "Went forward.";
                break;
            case "reload":
                await page.ReloadAsync();
                done = "Reloaded.";
                break;
            case "evaluate":
            {
                var result = await page.EvaluateAsync<JsonElement?>(Args.Required(args, "script"));
                return Text.Truncate(result?.ToString() ?? "undefined", maxChars);
            }
            case "tabs":
                return TabList(session);
            case "new_tab":
            {
                var fresh = await session.Page.Context.NewPageAsync();
                if (Args.Optional(args, "url") is { Length: > 0 } url) await session.GotoAsync(url);
                done = $"Opened tab {session.Pages.IndexOf(fresh)}.";
                break;
            }
            case "switch_tab":
            {
                var pages = session.Pages;
                var index = Args.Int(args, "tab") ?? throw new ArgumentException("switch_tab needs the 'tab' number.");
                if (index < 0 || index >= pages.Count) throw new ArgumentException($"No tab {index}. {TabList(session)}");
                await session.ActivateAsync(pages[index]);
                done = $"Switched to tab {index}.";
                break;
            }
            case "close_tab":
            {
                var pages = session.Pages;
                var index = Args.Int(args, "tab") ?? pages.IndexOf(page);
                if (index < 0 || index >= pages.Count) throw new ArgumentException($"No tab {index}. {TabList(session)}");
                await pages[index].CloseAsync();
                done = $"Closed tab {index}.";
                break;
            }
            default:
                throw new ArgumentException($"Unknown action '{action}'.");
        }

        await SettleAsync(session, ct);
        return await DescribeAsync(session, done, maxChars);
    }

    private async Task<ToolResult> ScreenshotAsync(BrowserSession session, JsonObject args)
    {
        if (saveImage is null) return ToolResult.Fail("Screenshots are not available in this setup.");
        var page = session.Page;
        byte[] bytes;
        var note = "";
        if (Args.Optional(args, "ref") is { Length: > 0 })
        {
            var target = await TargetAsync(session, args, "screenshot");
            bytes = await target.ScreenshotAsync(new LocatorScreenshotOptions { Type = ScreenshotType.Jpeg, Quality = 80 });
        }
        else
        {
            var options = new PageScreenshotOptions { Type = ScreenshotType.Jpeg, Quality = 75 };
            if (Args.Bool(args, "full_page"))
            {
                options.FullPage = true;
                var height = await page.EvaluateAsync<int>("() => Math.ceil(document.documentElement.scrollHeight)");
                if (height > MaxShotHeight)
                {
                    options.Clip = new Clip { X = 0, Y = 0, Width = session.Width, Height = MaxShotHeight };
                    note = $" The page is {height}px tall; only the first {MaxShotHeight}px are shown.";
                }
            }
            bytes = await page.ScreenshotAsync(options);
        }

        var image = saveImage(bytes, "image/jpeg", $"browser-{DateTime.Now:yyyyMMdd-HHmmss}.jpg");
        var text = $"Screenshot attached ({image.Size}).{note}\nURL: {page.Url}\nTitle: {await page.TitleAsync()}\n" +
                   "If you cannot see the image, use the snapshot action to read the page as text.";
        return ToolResult.WithImages(text, [image]);
    }

    private static async Task<ILocator> TargetAsync(BrowserSession session, JsonObject args, string verb)
    {
        var raw = Args.Required(args, "ref").Trim().Trim('[', ']');
        if (!RefPattern().IsMatch(raw)) throw new ArgumentException($"'{raw}' is not a ref. Refs look like e12 and come from the latest snapshot.");
        var locator = session.Page.Locator($"[data-sofia-ref=\"{raw}\"]").First;
        if (await locator.CountAsync() == 0)
            throw new ArgumentException($"Ref {raw} was not found. The page changed; run snapshot and use a fresh ref.");
        await session.ShowCursorAsync(locator, verb);
        return locator;
    }

    private static async Task<string> TypeAsync(BrowserSession session, JsonObject args)
    {
        var text = Args.Raw(args, "text") ?? throw new ArgumentException("type needs 'text'.");
        var page = session.Page;
        if (Args.Optional(args, "ref") is { Length: > 0 })
        {
            var target = await TargetAsync(session, args, "type");
            try
            {
                await target.FillAsync(text);
            }
            catch (PlaywrightException)
            {
                // Not a fillable element (custom editor): focus it and type key by key.
                await target.ClickAsync();
                await page.Keyboard.TypeAsync(text);
            }
        }
        else
        {
            await page.Keyboard.TypeAsync(text);
        }
        if (Args.Bool(args, "submit")) await page.Keyboard.PressAsync("Enter");
        return $"Typed {text.Length} characters{(Args.Bool(args, "submit") ? " and pressed Enter" : "")}.";
    }

    private static async Task<string> ScrollAsync(BrowserSession session, JsonObject args)
    {
        var page = session.Page;
        if (Args.Optional(args, "ref") is { Length: > 0 })
        {
            await (await TargetAsync(session, args, "scroll")).ScrollIntoViewIfNeededAsync();
            return "Scrolled the element into view.";
        }
        var direction = (Args.Optional(args, "direction") ?? "down").ToLowerInvariant();
        var amount = Args.Int(args, "amount") ?? (int)(session.Height * 0.8);
        switch (direction)
        {
            case "top":
                await page.EvaluateAsync("() => window.scrollTo(0, 0)");
                break;
            case "bottom":
                await page.EvaluateAsync("() => window.scrollTo(0, document.documentElement.scrollHeight)");
                break;
            case "up":
            case "down":
                await page.Mouse.MoveAsync(session.Width / 2f, session.Height / 2f);
                await page.Mouse.WheelAsync(0, direction == "up" ? -amount : amount);
                break;
            default:
                throw new ArgumentException("direction must be up, down, top or bottom.");
        }
        return $"Scrolled {direction}.";
    }

    private static async Task<string> WaitAsync(IPage page, JsonObject args, CancellationToken ct)
    {
        if (Args.Optional(args, "text") is { Length: > 0 } text)
        {
            await page.GetByText(text).First.WaitForAsync(new LocatorWaitForOptions { Timeout = 15_000 });
            return $"Text \"{Text.FirstLine(text, 60)}\" appeared.";
        }
        var seconds = Math.Clamp(Args.Int(args, "seconds") ?? 2, 1, 30);
        await Task.Delay(TimeSpan.FromSeconds(seconds), ct);
        return $"Waited {seconds}s.";
    }

    /// <summary>Lets a click or navigation take effect (new page, redirects, rendering) before the page is read again.</summary>
    private static async Task SettleAsync(BrowserSession session, CancellationToken ct)
    {
        await Task.Delay(350, ct);
        try
        {
            await session.Page.WaitForLoadStateAsync(LoadState.Load, new PageWaitForLoadStateOptions { Timeout = 5_000 });
        }
        catch (PlaywrightException)
        {
            // Slow page: read what is there.
        }
        await Task.Delay(150, ct);
    }

    private static string TabList(BrowserSession session)
    {
        var pages = session.Pages;
        var sb = new StringBuilder();
        for (var i = 0; i < pages.Count; i++)
            sb.Append(i).Append(pages[i] == session.Page ? " *" : "  ").Append(' ').AppendLine(pages[i].Url);
        return sb.ToString().TrimEnd();
    }

    private static async Task<string> DescribeAsync(BrowserSession session, string done, int maxChars)
    {
        var page = session.Page;
        var sb = new StringBuilder();
        if (done.Length > 0) sb.AppendLine(done);
        var notes = session.DrainNotes();
        if (notes.Length > 0) sb.AppendLine(notes);
        sb.AppendLine($"URL: {page.Url}");
        sb.AppendLine($"Title: {await page.TitleAsync()}");
        if (session.Pages.Count > 1) sb.AppendLine($"Tabs: {session.Pages.Count} (use the tabs action)");
        sb.AppendLine();
        sb.Append(await page.EvaluateAsync<string>(SnapshotScript));
        return Text.Truncate(sb.ToString(), maxChars);
    }

    [GeneratedRegex(@"^e\d+$")]
    private static partial Regex RefPattern();

    // Walks the visible DOM, tags interactive elements with data-sofia-ref and returns a compact outline.
    private const string SnapshotScript = """
() => {
  const SKIP = new Set(['SCRIPT', 'STYLE', 'NOSCRIPT', 'TEMPLATE', 'HEAD', 'META', 'LINK', 'SVG', 'CANVAS']);
  const INTERACTIVE = 'a[href],button,input:not([type=hidden]),select,textarea,summary,[role=button],[role=link],[role=checkbox],[role=radio],[role=tab],[role=menuitem],[role=switch],[role=option],[role=combobox],[role=textbox],[role=searchbox],[contenteditable=""],[contenteditable=true],[onclick]';
  const MAX_CHARS = 16000;
  document.querySelectorAll('[data-sofia-ref]').forEach(e => e.removeAttribute('data-sofia-ref'));
  const lines = [];
  let count = 0, chars = 0, truncated = false, last = '';
  const clean = (s, max = 160) => {
    s = (s || '').replace(/\s+/g, ' ').trim();
    return s.length > max ? s.slice(0, max) + '…' : s;
  };
  const emit = (line) => {
    if (!line || line === last) return;
    last = line;
    chars += line.length + 1;
    if (chars > MAX_CHARS) { truncated = true; return; }
    lines.push(line);
  };
  const visible = (el) => {
    const st = getComputedStyle(el);
    if (st.display === 'contents') return true;
    if (st.display === 'none' || st.visibility === 'hidden') return false;
    const r = el.getBoundingClientRect();
    return r.width > 0 && r.height > 0;
  };
  const roleOf = (el) => {
    const explicit = el.getAttribute('role');
    if (explicit) return explicit;
    const tag = el.tagName.toLowerCase();
    if (tag === 'a') return 'link';
    if (tag === 'button' || tag === 'summary') return 'button';
    if (tag === 'select') return 'combobox';
    if (tag === 'textarea') return 'textbox';
    if (tag === 'input') {
      const t = (el.type || 'text').toLowerCase();
      if (['checkbox', 'radio'].includes(t)) return t;
      if (['button', 'submit', 'reset', 'image'].includes(t)) return 'button';
      if (t === 'range') return 'slider';
      if (t === 'file') return 'file-input';
      return t === 'search' ? 'searchbox' : 'textbox';
    }
    return el.isContentEditable ? 'textbox' : 'clickable';
  };
  const nameOf = (el) => {
    const aria = el.getAttribute('aria-label');
    if (aria) return aria;
    const by = el.getAttribute('aria-labelledby');
    if (by) {
      const t = by.split(/\s+/).map(id => document.getElementById(id)?.textContent || '').join(' ');
      if (t.trim()) return t;
    }
    if (el.labels && el.labels.length) return [...el.labels].map(l => l.textContent).join(' ');
    const tag = el.tagName.toLowerCase();
    if (tag === 'input' && ['button', 'submit', 'reset'].includes(el.type)) return el.value;
    return el.innerText || el.textContent || el.getAttribute('alt') || el.getAttribute('title') || el.getAttribute('placeholder') || el.getAttribute('name') || '';
  };
  const describe = (el) => {
    const ref = 'e' + (++count);
    el.setAttribute('data-sofia-ref', ref);
    const tag = el.tagName.toLowerCase();
    let line = `[${ref}] ${roleOf(el)}`;
    const name = clean(nameOf(el), 100);
    if (name) line += ` "${name}"`;
    if (tag === 'a' && el.href) line += ` -> ${clean(el.href, 120)}`;
    if (tag === 'input' || tag === 'textarea') {
      const t = (el.type || 'text').toLowerCase();
      if (['checkbox', 'radio'].includes(t)) line += el.checked ? ' [checked]' : ' [unchecked]';
      else if (t === 'password') line += el.value ? ' value="••••"' : '';
      else if (el.value) line += ` value="${clean(el.value, 80)}"`;
      if (el.placeholder && !name.includes(el.placeholder)) line += ` placeholder="${clean(el.placeholder, 40)}"`;
    }
    if (tag === 'select') {
      const opts = [...el.options].slice(0, 20).map(o => (o.selected ? '*' : '') + clean(o.textContent, 40));
      line += ` options: ${opts.join(' | ')}`;
    }
    if (el.disabled) line += ' [disabled]';
    const expanded = el.getAttribute('aria-expanded');
    if (expanded) line += expanded === 'true' ? ' [expanded]' : ' [collapsed]';
    if (el.getAttribute('aria-selected') === 'true' || el.getAttribute('aria-current')) line += ' [current]';
    return line;
  };
  const walk = (node) => {
    if (truncated) return;
    if (node.nodeType === 3) { emit(clean(node.textContent, 300)); return; }
    if (node.nodeType !== 1) return;
    const el = node;
    if (SKIP.has(el.tagName.toUpperCase()) || el.getAttribute('aria-hidden') === 'true' || !visible(el)) return;
    const tag = el.tagName.toLowerCase();
    if (tag === 'iframe' || tag === 'frame') { emit(`[iframe ${clean(el.src, 80)}] (content not shown)`); return; }
    if (el.matches(INTERACTIVE)) { emit(describe(el)); return; }
    const heading = /^h([1-6])$/.exec(tag);
    if (heading && !el.querySelector(INTERACTIVE)) { emit('#'.repeat(+heading[1]) + ' ' + clean(el.innerText, 200)); return; }
    if (el.shadowRoot) for (const c of el.shadowRoot.childNodes) walk(c);
    for (const c of el.childNodes) walk(c);
  };
  walk(document.body);
  const maxScroll = Math.max(0, document.documentElement.scrollHeight - window.innerHeight);
  const head = `Viewport ${window.innerWidth}x${window.innerHeight}, scrolled ${Math.round(window.scrollY)} of ${Math.round(maxScroll)}px.`;
  return head + '\n' + (lines.join('\n') || '(the page has no visible content)') +
    (truncated ? '\n… snapshot truncated: scroll down or interact to see more.' : '');
}
""";
}

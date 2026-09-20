using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using ScreenTail.Shared.Schema;

namespace ScreenTail.Core.Detection.Registry;

/// <summary>
/// Which windows are a remote-support session, and which admin tools may be captured alongside one (ST-023).
///
/// This file decides what ScreenTail is allowed to photograph, which makes it the most security-sensitive
/// configuration in the client: an entry matching too broadly turns "capture the support session" into
/// "capture everything". So it is validated on load, every pattern is compiled once with a match timeout,
/// and a pattern that matches an empty title is rejected outright.
/// </summary>
public sealed class RemoteToolRegistry
{
    /// <summary>A pattern gets this long on one window title. They run on every foreground change.</summary>
    public static readonly TimeSpan MatchTimeout = TimeSpan.FromMilliseconds(50);

    private readonly Dictionary<string, ToolEntry> _byProcess;
    private readonly Dictionary<string, AdminToolEntry> _adminByProcess;

    private RemoteToolRegistry(RegistryDocument document)
    {
        Version = document.Version;
        Grace = TimeSpan.FromSeconds(document.GraceSeconds);
        Tools = document.Tools;
        BrowserPatterns = document.BrowserPatterns;
        AdminTools = document.AdminTools;

        // Process name is the common case and the fast path: a dictionary rather than a scan of patterns.
        _byProcess = new Dictionary<string, ToolEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (var tool in Tools)
        {
            foreach (var process in tool.Processes)
            {
                _byProcess[process] = tool;
            }
        }

        _adminByProcess = new Dictionary<string, AdminToolEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (var tool in AdminTools)
        {
            foreach (var process in tool.Processes)
            {
                _adminByProcess[process] = tool;
            }
        }
    }

    public string Version { get; }

    /// <summary>How long a session keeps running after the last remote-tool window loses focus.</summary>
    public TimeSpan Grace { get; }

    public IReadOnlyList<ToolEntry> Tools { get; }

    public IReadOnlyList<BrowserPatternEntry> BrowserPatterns { get; }

    public IReadOnlyList<AdminToolEntry> AdminTools { get; }

    public static RemoteToolRegistry Load(string json)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(json);
        var document = JsonSerializer.Deserialize<RegistryDocument>(json, Options)
            ?? throw new InvalidOperationException("The remote-tool registry is empty.");

        var problems = Validate(document);
        if (problems.Count > 0)
        {
            throw new InvalidOperationException($"The remote-tool registry is not usable: {string.Join("; ", problems)}");
        }

        return new RemoteToolRegistry(document);
    }

    /// <summary>
    /// Checks a registry before it is trusted. Used on load and by the policy sync (ST-047), so a bad entry
    /// from a tenant is rejected rather than quietly widening what gets captured.
    /// </summary>
    public static IReadOnlyList<string> Validate(RegistryDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        var problems = new List<string>();

        if (document.Tools.Count == 0)
        {
            problems.Add("no remote tools listed, so nothing would ever start a session");
        }

        if (document.GraceSeconds is < 5 or > 600)
        {
            problems.Add($"grace of {document.GraceSeconds}s is outside 5-600");
        }

        foreach (var tool in document.Tools)
        {
            if (tool.Processes.Count == 0 && tool.WindowClassPatterns.Count == 0)
            {
                problems.Add($"tool '{tool.Id}' matches nothing");
            }

            problems.AddRange(tool.WindowClassPatterns.SelectMany(p => PatternProblems(tool.Id, p)));
        }

        foreach (var pattern in document.BrowserPatterns)
        {
            problems.AddRange(PatternProblems(pattern.Id, pattern.TitlePattern));
            if (pattern.UrlPattern is { } url)
            {
                problems.AddRange(PatternProblems(pattern.Id, url));
            }
        }

        return problems;
    }

    /// <summary>The tool whose window this is, or null. Process name first; window class only if that misses.</summary>
    public ToolEntry? MatchTool(ForegroundWindowInfo window)
    {
        ArgumentNullException.ThrowIfNull(window);
        if (window.ProcessName is { } process && _byProcess.TryGetValue(process, out var byProcess))
        {
            return byProcess;
        }

        if (window.ClassName.Length == 0)
        {
            return null;
        }

        foreach (var tool in Tools)
        {
            foreach (var pattern in tool.CompiledClassPatterns)
            {
                if (SafeMatch(pattern, window.ClassName))
                {
                    return tool;
                }
            }
        }

        return null;
    }

    /// <summary>
    /// Whether a browser window is showing a remote session. The tab title is what we have; ST-043 will add
    /// the URL through UI Automation, and the pattern for it is already here so the registry needn't change.
    /// </summary>
    public BrowserPatternEntry? MatchBrowser(ForegroundWindowInfo window)
    {
        ArgumentNullException.ThrowIfNull(window);
        if (!BrowserTitles.IsBrowser(window.ProcessName))
        {
            return null;
        }

        var title = window.BrowserTabTitle ?? window.Title;
        if (title.Length == 0)
        {
            return null;
        }

        foreach (var pattern in BrowserPatterns)
        {
            if (!SafeMatch(pattern.CompiledTitle, title))
            {
                continue;
            }

            // The title got us this far, and on its own it is not evidence. Every browser entry declares
            // the addresses its tool actually lives at, and until 2026-09-20 nothing compared them: a
            // support email, a search result or the vendor's own documentation matched the same word,
            // put the whole browser in scope and started a session (INV-5, 2026-09-19 review).
            //
            // An address we cannot read is not a match. That is a feature turned off rather than a
            // wrong answer given, and Ctrl+Alt+R still starts a session by hand.
            if (pattern.CompiledUrl is { } url && !SafeMatch(url, window.BrowserUrl ?? string.Empty))
            {
                continue;
            }

            return pattern;
        }

        return null;
    }

    public AdminToolEntry? MatchAdminTool(ForegroundWindowInfo window)
    {
        ArgumentNullException.ThrowIfNull(window);
        return window.ProcessName is { } process && _adminByProcess.TryGetValue(process, out var tool) ? tool : null;
    }

    /// <summary>A pattern that runs out of its budget matches nothing, rather than stalling the watcher.</summary>
    private static bool SafeMatch(Regex pattern, string text)
    {
        try
        {
            return pattern.IsMatch(text);
        }
        catch (RegexMatchTimeoutException)
        {
            return false;
        }
    }

    private static string[] PatternProblems(string id, string pattern)
    {
        try
        {
            var regex = new Regex(pattern, RegexOptions.CultureInvariant, MatchTimeout);

            // A pattern matching empty text matches every window, which would put everything in scope.
            return regex.IsMatch(string.Empty) ? [$"'{id}' has a pattern that matches everything"] : [];
        }
        catch (ArgumentException ex)
        {
            return [$"'{id}' has an invalid pattern: {ex.Message}"];
        }
    }

    internal static JsonSerializerOptions Options { get; } = new(JsonSerializerDefaults.Web)
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };
}

public sealed class RegistryDocument
{
    [JsonPropertyName("version")]
    public string Version { get; init; } = "unknown";

    [JsonPropertyName("grace_seconds")]
    public int GraceSeconds { get; init; } = 90;

    [JsonPropertyName("tools")]
    public IReadOnlyList<ToolEntry> Tools { get; init; } = [];

    [JsonPropertyName("browser_patterns")]
    public IReadOnlyList<BrowserPatternEntry> BrowserPatterns { get; init; } = [];

    [JsonPropertyName("admin_tools")]
    public IReadOnlyList<AdminToolEntry> AdminTools { get; init; } = [];
}

public sealed class ToolEntry
{
    private IReadOnlyList<Regex>? _compiled;

    [JsonPropertyName("id")]
    public required string Id { get; init; }

    [JsonPropertyName("kind")]
    public RemoteToolKind Kind { get; init; } = RemoteToolKind.Other;

    [JsonPropertyName("display_name")]
    public required string DisplayName { get; init; }

    [JsonPropertyName("processes")]
    public IReadOnlyList<string> Processes { get; init; } = [];

    [JsonPropertyName("window_class_patterns")]
    public IReadOnlyList<string> WindowClassPatterns { get; init; } = [];

    /// <summary>Where the tool's client version comes from. "file" means the executable's version resource.</summary>
    [JsonPropertyName("version_from")]
    public string? VersionFrom { get; init; }

    /// <summary>Compiled once, on first use, and reused for every foreground change afterwards.</summary>
    internal IReadOnlyList<Regex> CompiledClassPatterns =>
        _compiled ??= [.. WindowClassPatterns.Select(p => new Regex(p, RegexOptions.CultureInvariant | RegexOptions.Compiled, RemoteToolRegistry.MatchTimeout))];
}

public sealed class BrowserPatternEntry
{
    private Regex? _title;

    [JsonPropertyName("id")]
    public required string Id { get; init; }

    [JsonPropertyName("display_name")]
    public required string DisplayName { get; init; }

    [JsonPropertyName("title_pattern")]
    public required string TitlePattern { get; init; }

    /// <summary>
    /// The addresses this tool actually lives at.
    ///
    /// Required for a match when it is present, because a tab title is not evidence of what a tab is.
    /// Nothing reads a browser's address bar yet (ST-043), so today this means browser entries do not
    /// match at all — a feature turned off rather than a wrong answer given.
    /// </summary>
    [JsonPropertyName("url_pattern")]
    public string? UrlPattern { get; init; }

    private Regex? _url;

    internal Regex CompiledTitle =>
        _title ??= new Regex(TitlePattern, RegexOptions.CultureInvariant | RegexOptions.Compiled, RemoteToolRegistry.MatchTimeout);

    /// <summary>Null when the entry names no addresses, in which case the title is all there is.</summary>
    internal Regex? CompiledUrl =>
        UrlPattern is null
            ? null
            : _url ??= new Regex(UrlPattern, RegexOptions.CultureInvariant | RegexOptions.Compiled, RemoteToolRegistry.MatchTimeout);
}

public sealed class AdminToolEntry
{
    [JsonPropertyName("id")]
    public required string Id { get; init; }

    [JsonPropertyName("display_name")]
    public required string DisplayName { get; init; }

    [JsonPropertyName("processes")]
    public IReadOnlyList<string> Processes { get; init; } = [];
}

using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using ScreenTail.Core.Detection;

namespace ScreenTail.Core.Privacy;

/// <param name="Id">The rule that matched, for the audit row and the diagnostics panel. Never the title.</param>
/// <param name="DisplayName">What the HUD says after "Paused: excluded app" — a rule name, not a window name.</param>
public sealed record Exclusion(string Id, string DisplayName);

/// <summary>
/// Windows that must never be photographed, whatever the capture scope says (ST-043, INV-6).
///
/// Scope (ST-023) answers "is this part of the support session"; this answers "is this something we must
/// not look at even if it is". They are different questions and a window can fail this one while passing
/// that one — a technician looking up a customer's password in 1Password during a ScreenConnect session is
/// doing support work, and is the exact case this exists for.
///
/// <b>Every ambiguity resolves towards excluding.</b> A regex that times out, a title we could not read, a
/// process we could not name: each is treated as a match. That is the opposite of
/// <see cref="Detection.Registry.RemoteToolRegistry"/>, where a timeout means "not a remote tool" and the
/// cost is a session that does not start. Here the cost of guessing wrong is a screenshot of somebody's
/// password vault in a ticket, so the two have to lean opposite ways and this one says so out loud.
/// </summary>
public sealed class ExclusionList
{
    /// <summary>
    /// Long enough for any honest pattern over a window title, short enough that a bad one cannot stall
    /// the foreground path. Matches the registry's budget for the same reason.
    /// </summary>
    public static readonly TimeSpan MatchTimeout = TimeSpan.FromMilliseconds(50);

    private static readonly JsonSerializerOptions Format = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    private readonly Dictionary<string, Exclusion> _byProcess = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<(Regex Pattern, Exclusion Rule)> _titles = [];
    private readonly List<(string Fragment, Exclusion Rule)> _urls = [];

    private ExclusionList(ExclusionDocument document)
    {
        Version = document.Version;

        foreach (var entry in document.Processes)
        {
            var rule = new Exclusion(entry.Id, entry.DisplayName);
            foreach (var process in entry.Processes)
            {
                // Windows reports a process without its extension; a tenant's list may include one.
                _byProcess[Path.GetFileNameWithoutExtension(process)] = rule;
            }
        }

        foreach (var entry in document.TitlePatterns)
        {
            _titles.Add((new Regex(entry.Pattern, RegexOptions.CultureInvariant, MatchTimeout), new Exclusion(entry.Id, entry.DisplayName)));
        }

        foreach (var entry in document.UrlFragments)
        {
            var rule = new Exclusion(entry.Id, entry.DisplayName);
            foreach (var fragment in entry.Fragments)
            {
                _urls.Add((fragment, rule));
            }
        }
    }

    public string Version { get; }

    public int Rules => _byProcess.Count + _titles.Count + _urls.Count;

    /// <summary>How many separate applications are excluded by process name — ST-043 wants at least ten.</summary>
    public int ExcludedApplications => _byProcess.Values.Select(r => r.Id).Distinct(StringComparer.Ordinal).Count();

    public static ExclusionList Load(string json)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(json);
        var document = JsonSerializer.Deserialize<ExclusionDocument>(json, Format)
            ?? throw new InvalidOperationException("The exclusion list is empty.");

        var problems = Validate(document);
        if (problems.Count > 0)
        {
            // Refused rather than partially applied. A list that half-loaded would silently stop excluding
            // whichever rules came after the broken one, and nothing would say so.
            throw new InvalidOperationException($"The exclusion list is not usable: {string.Join("; ", problems)}");
        }

        return new ExclusionList(document);
    }

    public static IReadOnlyList<string> Validate(ExclusionDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        var problems = new List<string>();

        foreach (var entry in document.Processes.Where(e => e.Processes.Count == 0))
        {
            problems.Add($"'{entry.Id}' excludes no processes");
        }

        foreach (var entry in document.TitlePatterns)
        {
            try
            {
                _ = new Regex(entry.Pattern, RegexOptions.CultureInvariant, MatchTimeout);
            }
            catch (ArgumentException ex)
            {
                problems.Add($"'{entry.Id}' has an unusable pattern: {ex.Message}");
            }
        }

        foreach (var entry in document.UrlFragments.Where(e => e.Fragments.Count == 0))
        {
            problems.Add($"'{entry.Id}' excludes no addresses");
        }

        return problems;
    }

    /// <summary>
    /// The rule that says this window must not be captured, or null when none does.
    ///
    /// A window with no process name and no title is excluded. That combination means Windows would tell
    /// us nothing about what is in front — a secure desktop, a window we may not open, a process that just
    /// died — and "we could not find out" is not a reason to photograph something.
    /// </summary>
    public Exclusion? Match(ForegroundWindowInfo window)
    {
        ArgumentNullException.ThrowIfNull(window);

        if (window.ProcessName is { Length: > 0 } process)
        {
            // Stripped on both sides. Windows reports a process without its extension, but the name can
            // also reach us from a tenant's own list or a policy file, where somebody wrote "1Password.exe"
            // — and an exclusion that misses because of four characters is an exclusion that did nothing.
            if (_byProcess.TryGetValue(Path.GetFileNameWithoutExtension(process), out var byProcess))
            {
                return byProcess;
            }
        }
        else if (window.Title.Length == 0)
        {
            return new Exclusion("unknown-window", "an application Windows would not identify");
        }

        var title = window.BrowserTabTitle ?? window.Title;
        if (title.Length == 0)
        {
            return null;
        }

        foreach (var (pattern, rule) in _titles)
        {
            try
            {
                if (pattern.IsMatch(title))
                {
                    return rule;
                }
            }
            catch (RegexMatchTimeoutException)
            {
                // Excluded, not ignored. A title this pattern could not finish reading is a title nobody
                // has checked, and the cost of being wrong here is a screenshot of a password vault.
                return rule;
            }
        }

        foreach (var (fragment, rule) in _urls)
        {
            if (title.Contains(fragment, StringComparison.OrdinalIgnoreCase))
            {
                return rule;
            }
        }

        return null;
    }
}

public sealed class ExclusionDocument
{
    public string Version { get; init; } = "unknown";

    public IReadOnlyList<ExcludedProcesses> Processes { get; init; } = [];

    public IReadOnlyList<ExcludedTitle> TitlePatterns { get; init; } = [];

    public IReadOnlyList<ExcludedUrls> UrlFragments { get; init; } = [];
}

public sealed class ExcludedProcesses
{
    public required string Id { get; init; }

    public required string DisplayName { get; init; }

    public IReadOnlyList<string> Processes { get; init; } = [];
}

public sealed class ExcludedTitle
{
    public required string Id { get; init; }

    public required string DisplayName { get; init; }

    public required string Pattern { get; init; }
}

public sealed class ExcludedUrls
{
    public required string Id { get; init; }

    public required string DisplayName { get; init; }

    public IReadOnlyList<string> Fragments { get; init; } = [];
}

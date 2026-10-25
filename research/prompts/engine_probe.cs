#:project ../../backend/src/ScreenTail.Api/ScreenTail.Api.csproj
#:property PublishAot=false
#:property InvariantGlobalization=true
#:property JsonSerializerIsReflectionEnabledByDefault=true
#:property TreatWarningsAsErrors=false
#:property NoWarn=$(NoWarn);CA1707;CA1305;CA1307;CA1310;CA1847;CA1861;MSB3277

// What .NET reads and finds, for research/prompts/unicode_tables.py and differential.py to compare with
// Python's re. It runs with InvariantGlobalization, as the API does in production.
//
//   dotnet run research/prompts/engine_probe.cs -- request.json answer.json
//
// The request names a mode:
// - "classes": which BMP characters .NET's Regex takes as \s, as \d, and as a word character where \b
//   is concerned, and which non-ASCII characters an ASCII letter matches under IgnoreCase. Nothing in
//   the validator is used, so this works before unicode-tables.json is complete.
// - "single": for each pattern, which of the given characters it matches whole, compiled with and
//   without IgnoreCase as the validator compiles its patterns; with "surrogates", each lone surrogate
//   is a character after the given ones.
// - "read": DraftValidator.Read of each text, and nothing else.
// - "cases": for each text, DraftValidator.Read of it, the instruction the validator finds, main's
//   Directive verdict on the text and on the text as read, and whether DraftValidator refuses a draft
//   whose result is the text.
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using ScreenTail.Api.Summarize;

var request = JsonDocument.Parse(File.ReadAllText(args[0])).RootElement;
var mode = request.GetProperty("mode").GetString();
object answer = mode switch
{
    "classes" => Classes(),
    "single" => Single(request),
    "cases" => Cases(request),
    "read" => request.GetProperty("texts").EnumerateArray().Select(t => DraftValidator.Read(t.GetString()!)).ToList(),
    _ => throw new ArgumentException($"Unknown mode '{mode}'."),
};
File.WriteAllText(args[1], JsonSerializer.Serialize(answer));

static object Classes()
{
    var bmp = Enumerable.Range(0, 0x10000).Where(c => c is < 0xD800 or > 0xDFFF).ToList();
    var space = new Regex(@"^\s$", RegexOptions.CultureInvariant);
    var digit = new Regex(@"^\d$", RegexOptions.CultureInvariant);
    var boundary = new Regex(@"^a\b", RegexOptions.CultureInvariant);
    var letters = new Dictionary<string, List<int>>();
    for (var l = 'a'; l <= 'z'; l++)
    {
        var r = new Regex("^" + l + "$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        letters[l.ToString()] = [.. bmp.Where(c => c > 0x7F && r.IsMatch(((char)c).ToString()))];
    }

    return new
    {
        runtime = Environment.Version.ToString(),
        culture = CultureInfo.CurrentCulture.Name,
        space = bmp.Where(c => space.IsMatch(((char)c).ToString())).ToList(),
        white_space = bmp.Where(c => char.IsWhiteSpace((char)c)).ToList(),
        digit = bmp.Where(c => digit.IsMatch(((char)c).ToString())).ToList(),
        word = bmp.Where(c => !boundary.IsMatch("a" + (char)c)).ToList(),
        letters,
    };
}

static object Single(JsonElement request)
{
    // Lone surrogates do not survive JSON, so the request asks for them by name and they are made here.
    var characters = request.GetProperty("characters").EnumerateArray().Select(c => c.GetString()!).ToList();
    if (request.TryGetProperty("surrogates", out var surrogates) && surrogates.GetBoolean())
    {
        characters.AddRange(Enumerable.Range(0xD800, 0x800).Select(c => ((char)c).ToString()));
    }

    var found = new Dictionary<string, List<int>>();
    foreach (var p in request.GetProperty("patterns").EnumerateArray())
    {
        var name = p.GetProperty("name").GetString()!;
        var options = RegexOptions.CultureInvariant
            | (p.GetProperty("ignore_case").GetBoolean() ? RegexOptions.IgnoreCase : RegexOptions.None);
        var whole = new Regex("^(?:" + p.GetProperty("pattern").GetString()! + ")$", options);
        found[name] = [.. characters.Select((c, i) => (c, i)).Where(x => whole.IsMatch(x.c)).Select(x => x.i)];
    }

    return found;
}

static object Cases(JsonElement request)
{
    var texts = request.GetProperty("texts").EnumerateArray().Select(t => t.GetString()!).ToList();
    // The transcript of research/fixtures/handcrafted/spooler-stopped-screenconnect, which checks.py
    // judges the same cases against, as SharedHardeningCasesTests does.
    var bundle = new SummarizeBundle
    {
        SessionId = "s1",
        DurationMs = 10 * 60 * 1000,
        Frames = [],
        Transcript =
        [
            new BundleSegment("t-0001", 9_500, "Spooler service is stopped, that's why nothing is printing."),
            new BundleSegment("t-0002", 19_000, "Started it again and set it to restart on failure."),
            new BundleSegment("t-0003", 34_000, "Test page came out fine, so the queue is clear now."),
        ],
    };
    return texts.Select(text => new
    {
        read = DraftValidator.Read(text),
        instruction = DraftValidator.Instruction(text) is not null,
        main = MainDirective.IsMatch(Blanked(text)),
        main_on_read = MainDirective.IsMatch(Blanked(DraftValidator.Read(text))),
        refuse = DraftValidator.Check(Draft(text), bundle).Count > 0,
    }).ToList();
}

static string Blanked(string text) =>
    new[] { "[REDACTED]", "[CARD]", "[SSN]", "[SECRET]", "[EMAIL]" }.Aggregate(text, (t, m) => t.Replace(m, " ", StringComparison.Ordinal));

static DraftJson Draft(string result) => new()
{
    Problem = "Nothing would print from the reception workstation.",
    Steps = [],
    Result = result,
    FollowUps = [],
    SuggestedTitle = "Printer offline",
    SuggestedTimeMinutes = 10,
    KbCandidate = false,
    KbReason = "A one-off.",
    Source = "cloud",
    PromptVersion = DraftValidator.PromptVersion,
};

/// <summary>
/// The instruction rule as production ran it before ST-061's shared rules, copied verbatim from
/// DraftValidator.cs at main b989436, with its options. The probe runs with InvariantGlobalization, so
/// IgnoreCase here is production's.
/// </summary>
internal static partial class Program
{
    private static readonly Regex MainDirective = new(
        @"(?:https?://|www\.)\S+"
        + @"|\b(?:Set-ExecutionPolicy|powershell|cmd\.exe|regedit|curl|wget|iwr|irm|invoke-webrequest"
        + @"|invoke-restmethod|invoke-expression|iex|certutil|bitsadmin|mshta|rundll32)\b"
        + @"|\b(?:disable|turn\s+off|uninstall|remove)\s+(?:the\s+)?(?:antivirus|defender|firewall|edr|mfa|two-factor)\b"
        + @"|\b(?:download|install|run|execute|disable|uninstall|delete)\b\s+(?:the\s+|your\s+|windows\s+)?\S+",
        RegexOptions.Compiled | RegexOptions.IgnoreCase,
        TimeSpan.FromSeconds(1));
}

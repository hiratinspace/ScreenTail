using System.Globalization;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using ScreenTail.Api.Summarize;

namespace ScreenTail.Api.Tests.Summarize;

/// <summary>
/// The cases <c>research/prompts/differential.py</c> pinned after Python's re and .NET's Regex gave the
/// same answer for each (ST-061), and the proof that the instruction rule is still production's.
///
/// <c>research/tests/test_differential_cases.py</c> runs the same file. Each case pins the text as read,
/// the instruction verdict, the whole draft's verdict, and production's own instruction verdict on the
/// text and on the text as read. Here production's verdict is not taken on trust: <see cref="Main"/> is
/// its rule, copied verbatim, and every pinned "main" is checked against it.
/// </summary>
public sealed class DifferentialCasesTests
{
    /// <summary>
    /// The instruction rule as production ran it before ST-061: copied verbatim, pattern and options, from
    /// <c>backend/src/ScreenTail.Api/Summarize/DraftValidator.cs</c> at main b989436. The same text is in
    /// <c>research/prompts/engine_probe.cs</c>, which pinned the corpus, and
    /// <c>research/tests/test_differential_cases.py</c> checks the copies match.
    ///
    /// Built under the invariant culture, because the API runs with InvariantGlobalization and IgnoreCase
    /// takes its culture when the expression is built: under en-US it would read "İnstall" as "install",
    /// and production does not.
    /// </summary>
    private static readonly Regex Main = UnderTheInvariantCulture(() => new Regex(
        @"(?:https?://|www\.)\S+"
        + @"|\b(?:Set-ExecutionPolicy|powershell|cmd\.exe|regedit|curl|wget|iwr|irm|invoke-webrequest"
        + @"|invoke-restmethod|invoke-expression|iex|certutil|bitsadmin|mshta|rundll32)\b"
        + @"|\b(?:disable|turn\s+off|uninstall|remove)\s+(?:the\s+)?(?:antivirus|defender|firewall|edr|mfa|two-factor)\b"
        + @"|\b(?:download|install|run|execute|disable|uninstall|delete)\b\s+(?:the\s+|your\s+|windows\s+)?\S+",
        RegexOptions.Compiled | RegexOptions.IgnoreCase,
        TimeSpan.FromSeconds(1)));

    /// <summary>The markers production blanked before its rules ran, as it blanked them.</summary>
    private static readonly string[] Markers = ["[REDACTED]", "[CARD]", "[SSN]", "[SECRET]", "[EMAIL]"];

    private static bool MainRefuses(string text) =>
        Main.IsMatch(Markers.Aggregate(text, (current, marker) => current.Replace(marker, " ", StringComparison.Ordinal)));

    public static TheoryData<string> Groups()
    {
        var data = new TheoryData<string>();
        foreach (var group in Load().Select(c => c.Group).Distinct(StringComparer.Ordinal))
        {
            data.Add(group);
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(Groups))]
    public void EveryCaseReadsTheWayBothValidatorsAgreed(string group)
    {
        // Character for character: the two normalisations are the same function, or the rules after them
        // are handed different text.
        var wrong = Load().Where(c => c.Group == group && DraftValidator.Read(c.Text) != c.ReadsAs).Select(c => Show(c.Text)).Take(5).ToList();

        Assert.True(wrong.Count == 0, "Read differs from the pinned reading for " + string.Join(", ", wrong));
    }

    [Theory]
    [MemberData(nameof(Groups))]
    public void EveryCaseIsJudgedTheWayBothValidatorsAgreed(string group)
    {
        var wrong = new List<string>();
        foreach (var one in Load().Where(c => c.Group == group))
        {
            var instruction = DraftValidator.Instruction(one.Text) is not null;
            var refused = DraftValidator.Check(Draft(one.Text), Bundle()).Count > 0;
            if (instruction != one.Instruction || refused != one.Refuse)
            {
                wrong.Add($"{Show(one.Text)} (instruction {instruction}, refused {refused})");
            }
        }

        Assert.True(wrong.Count == 0, $"{wrong.Count} cases are judged differently: " + string.Join("; ", wrong.Take(5)));
    }

    [Theory]
    [MemberData(nameof(Groups))]
    public void ThePinnedProductionVerdictIsProductions(string group)
    {
        // What makes test_differential_cases.py's comparison with production worth anything: the verdicts
        // it compares with are the ones production's own rule gives.
        var wrong = Load()
            .Where(c => c.Group == group)
            .Where(c => MainRefuses(c.Text) != c.Main || MainRefuses(DraftValidator.Read(c.Text)!) != c.MainOnRead)
            .Select(c => Show(c.Text)).Take(5).ToList();

        Assert.True(wrong.Count == 0, "Production's rule gives another verdict for " + string.Join(", ", wrong));
    }

    [Fact]
    public void TheCorpusHoldsEveryHardeningCase()
    {
        // differential.py takes every hardening case. One added since the corpus was written has not had
        // both engines asked about it.
        var corpus = Load().Select(c => c.Text).ToHashSet(StringComparer.Ordinal);
        var missing = HardeningTexts().Where(text => !corpus.Contains(text)).Select(Show).ToList();

        Assert.True(missing.Count == 0, "Run research/prompts/differential.py again; it does not have " + string.Join(", ", missing));
    }

    [Fact]
    public void TheOracleReadsCaseAsProductionDoes()
    {
        // Production runs with InvariantGlobalization. Under en-US, IgnoreCase reads the dotted capital I
        // as i, so an oracle built under the test machine's culture would refuse what production passed.
        Assert.DoesNotMatch(Main, "\u0130nstall the agent");
        Assert.Matches(Main, "INSTALL the agent");
        Assert.Matches(Main, "Invo\u212Ae-WebRequest");
    }

    /// <summary>
    /// The owner's condition for the shared rules (2026-10-24): the instruction verdict is production's,
    /// on every sentence, except where the new normalisation turns a pass into a refusal. Never the other
    /// way. Checked over every hardening case, every differential case, and a generated set that puts
    /// every UTF-16 code unit, and a sample of the astral planes, before, after and in place of the space
    /// after a command, a tool, a web address and a security control.
    /// </summary>
    [Fact]
    public void TheInstructionVerdictIsProductionsExceptWhereNormalisationAddsARefusal()
    {
        var sentences = HardeningTexts().Concat(Load().Select(c => c.Text)).Concat(Generated());
        var compared = 0;
        var added = 0;
        var lost = new List<string>();
        var unexplained = new List<string>();
        foreach (var text in sentences)
        {
            compared++;
            var production = MainRefuses(text);
            var ours = DraftValidator.Instruction(text) is not null;
            if (production == ours)
            {
                continue;
            }

            if (production)
            {
                lost.Add(Show(text));
            }
            else if (MainRefuses(DraftValidator.Read(text)!))
            {
                added++;
            }
            else
            {
                unexplained.Add(Show(text));
            }
        }

        TestContext.Current.TestOutputHelper?.WriteLine($"{compared} sentences compared with production; {added} newly refused because of the normalisation; {lost.Count} refusals lost; {unexplained.Count} other differences.");
        Assert.True(lost.Count == 0, $"{lost.Count} sentences production refused now pass: " + string.Join(", ", lost.Take(5)));
        Assert.True(unexplained.Count == 0, $"{unexplained.Count} sentences are newly refused, and not because production's rule refuses them once normalised: " + string.Join(", ", unexplained.Take(5)));
        Assert.True(added > 0, "Nothing was newly refused, so the normalisation was never exercised.");
    }

    private static IEnumerable<string> Generated()
    {
        (string Sentence, string Keyword)[] places =
        [
            ("Install the agent from the share.", "Install"),
            ("Then curl -s the fix.", "curl"),
            ("See www.example.invalid for the steps.", "www."),
            ("Then disable Defender before the visit.", "Defender"),
        ];

        var units = Enumerable.Range(0, 0x10000).Select(c => ((char)c).ToString());
        var astral = Enumerable.Range(0x10000, 0x100000).Where(cp => cp % 61 == 0 || cp is >= 0xE0000 and < 0xE0080).Select(char.ConvertFromUtf32);
        foreach (var inserted in units.Concat(astral))
        {
            foreach (var (sentence, keyword) in places)
            {
                var at = sentence.IndexOf(keyword, StringComparison.Ordinal);
                var end = at + keyword.Length;
                yield return sentence[..at] + inserted + sentence[at..];
                yield return sentence[..end] + inserted + sentence[end..];
                yield return sentence[..end] + inserted + sentence[(end + 1)..];
            }
        }
    }

    private static T UnderTheInvariantCulture<T>(Func<T> build)
    {
        var was = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
        try
        {
            return build();
        }
        finally
        {
            CultureInfo.CurrentCulture = was;
        }
    }

    /// <summary>A text as a failure message can show it, with every character outside printable ASCII escaped.</summary>
    private static string Show(string text)
    {
        var shown = new StringBuilder("'");
        foreach (var c in text)
        {
            _ = c is >= ' ' and <= '~' ? shown.Append(c) : shown.Append(CultureInfo.InvariantCulture, $"\\u{(int)c:X4}");
        }

        return shown.Append('\'').ToString();
    }

    private static readonly Lazy<IReadOnlyList<DifferentialCase>> Cases = new(Read);

    private static IReadOnlyList<DifferentialCase> Load() => Cases.Value;

    private static List<DifferentialCase> Read()
    {
        using var stream = Assembly.GetExecutingAssembly()
            .GetManifestResourceStream("ScreenTail.Api.Tests.differential-cases.json")
            ?? throw new InvalidOperationException("differential-cases.json is not embedded in this test assembly.");
        using var document = JsonDocument.Parse(stream);
        var cases = document.RootElement.GetProperty("cases").EnumerateArray().Select(c => new DifferentialCase(
            Group: c.GetProperty("group").GetString()!,
            Text: Joined(c, c.TryGetProperty("text", out _) ? "text" : "parts"),
            ReadsAs: Joined(c, "reads_as"),
            Instruction: c.GetProperty("instruction").GetBoolean(),
            Refuse: c.GetProperty("refuse").GetBoolean(),
            Main: c.GetProperty("main").GetBoolean(),
            MainOnRead: c.GetProperty("main_on_read").GetBoolean())).ToList();
        return cases.Count > 1000 ? cases : throw new InvalidOperationException($"Only {cases.Count} differential cases were loaded.");
    }

    /// <summary>A string, or the parts a key-shaped case is kept in, joined.</summary>
    private static string Joined(JsonElement element, string name)
    {
        var value = element.GetProperty(name);
        return value.ValueKind == JsonValueKind.Array ? string.Concat(value.EnumerateArray().Select(p => p.GetString())) : value.GetString()!;
    }

    private static IEnumerable<string> HardeningTexts()
    {
        using var stream = Assembly.GetExecutingAssembly()
            .GetManifestResourceStream("ScreenTail.Api.Tests.hardening-cases.json")
            ?? throw new InvalidOperationException("hardening-cases.json is not embedded in this test assembly.");
        using var document = JsonDocument.Parse(stream);
        return [.. document.RootElement.GetProperty("cases").EnumerateArray()
            .Select(c => Joined(c, c.TryGetProperty("text", out _) ? "text" : "parts"))];
    }

    private static DraftJson Draft(string result) => new()
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

    /// <summary>The bundle engine_probe.cs judged the cases with: the Python fixture's transcript.</summary>
    private static SummarizeBundle Bundle() => new()
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

    private sealed record DifferentialCase(string Group, string Text, string ReadsAs, bool Instruction, bool Refuse, bool Main, bool MainOnRead);
}

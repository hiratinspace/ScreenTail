using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using ScreenTail.Api.Summarize;

namespace ScreenTail.Api.Tests.Summarize;

/// <param name="Text">The draft's result field: prose a technician reads and may publish to a customer.</param>
/// <param name="Refuse">Whether the post-conditions must reject it.</param>
/// <param name="Why">For whoever reads a failure. Never asserted on.</param>
public sealed record HardeningCase(
    [property: JsonPropertyName("text")] string Text,
    [property: JsonPropertyName("refuse")] bool Refuse,
    [property: JsonPropertyName("why")] string Why);

/// <summary>
/// The cases this validator and <c>research/prompts/checks.py</c> must answer the same way (ST-063).
///
/// Both files claim to mirror each other, in comments, and both had drifted by the time anyone checked:
/// on 2026-09-19 a review found that curly quotes, "the password was reset to X", bare AWS and GitHub
/// key shapes, and <c>iex</c>, <c>iwr</c>, <c>certutil</c> and <c>bitsadmin</c> all passed here and were
/// caught there. A comment saying two things agree is not a mechanism.
///
/// <c>research/prompts/hardening-cases.json</c> is the mechanism. Both suites read it, so a case added
/// on either side fails both until both know it. Which rule fires is deliberately not asserted: that
/// would make the two agree about their internals rather than about their answers.
/// </summary>
public sealed class SharedHardeningCasesTests
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static TheoryData<HardeningCase> Cases()
    {
        var data = new TheoryData<HardeningCase>();
        foreach (var one in Load())
        {
            data.Add(one);
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void ADraftIsJudgedTheWayTheSharedFileSaysItShouldBe(HardeningCase shared)
    {
        ArgumentNullException.ThrowIfNull(shared);
        var refused = DraftValidator.Check(Draft(shared.Text), Bundle()).Count > 0;

        Assert.True(
            refused == shared.Refuse,
            (shared.Refuse ? "Expected a refusal: " : "Expected this to pass: ") + shared.Why);
    }

    [Fact]
    public void TheSharedFileIsNotEmptyAndHasBothAnswersInIt()
    {
        // A file that failed to load, or one that only says no, would make every case above vacuous.
        var cases = Load();

        Assert.True(cases.Count >= 20, $"Only {cases.Count} shared cases were loaded.");
        Assert.Contains(cases, one => one.Refuse);
        Assert.Contains(cases, one => !one.Refuse);
    }

    private static IReadOnlyList<HardeningCase> Load()
    {
        using var stream = Assembly.GetExecutingAssembly()
            .GetManifestResourceStream("ScreenTail.Api.Tests.hardening-cases.json")
            ?? throw new InvalidOperationException("hardening-cases.json is not embedded in this test assembly.");

        return JsonSerializer.Deserialize<CaseFile>(stream, Json)?.Cases
            ?? throw new InvalidOperationException("hardening-cases.json could not be read.");
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

    private static SummarizeBundle Bundle() => new()
    {
        SessionId = "s1",
        DurationMs = 10 * 60 * 1000,
        Frames = [new BundleFrame("f1", 1_000, "Services Print Spooler Stopped")],
        Transcript = [new BundleSegment("t1", 1_200, "clearing the queue now")],
    };

    private sealed record CaseFile(
        [property: JsonPropertyName("cases")] IReadOnlyList<HardeningCase> Cases);
}

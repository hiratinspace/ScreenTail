using System.Reflection;
using System.Text.Json;
using ScreenTail.Api.Summarize;

namespace ScreenTail.Api.Tests.Summarize;

/// <summary>
/// The validator's patterns and word lists are the ones in <c>research/prompts/shared-rules.json</c>
/// (ST-061, 2026-10-12 review).
///
/// <c>checks.py</c> reads that file; <see cref="DraftRules"/> keeps the same values as constants so the
/// service has nothing to load. This is what makes "the same rules" a fact rather than a comment: the
/// 2026-10-12 review found the server's key shapes and cue words narrower than the client's, with both
/// validators claiming to mirror each other.
/// </summary>
public sealed class SharedRulesTests
{
    public static TheoryData<string> Names()
    {
        var data = new TheoryData<string>();
        foreach (var name in Shared().Keys)
        {
            data.Add(name);
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(Names))]
    public void EveryRuleInTheSharedFileIsTheOneThisValidatorUses(string name)
    {
        var ours = DraftRules.AsShared();

        Assert.True(ours.ContainsKey(name), $"shared-rules.json has '{name}' and DraftRules does not.");
        Assert.Equal(Shared()[name], ours[name]);
    }

    [Fact]
    public void EveryRuleThisValidatorUsesIsInTheSharedFile()
    {
        // The other direction: a rule added here and not there is a rule checks.py does not know.
        var shared = Shared();

        Assert.All(DraftRules.AsShared().Keys, name => Assert.True(shared.ContainsKey(name), $"DraftRules has '{name}' and shared-rules.json does not."));
    }

    private static Dictionary<string, IReadOnlyList<string>> Shared()
    {
        using var stream = Assembly.GetExecutingAssembly()
            .GetManifestResourceStream("ScreenTail.Api.Tests.shared-rules.json")
            ?? throw new InvalidOperationException("shared-rules.json is not embedded in this test assembly.");
        using var document = JsonDocument.Parse(stream);

        var rules = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
        foreach (var property in document.RootElement.EnumerateObject().Where(p => !p.Name.StartsWith('_')))
        {
            rules[property.Name] = property.Value.ValueKind == JsonValueKind.Array
                ? [.. property.Value.EnumerateArray().Select(item => item.GetString()!)]
                : [property.Value.GetString()!];
        }

        return rules;
    }
}

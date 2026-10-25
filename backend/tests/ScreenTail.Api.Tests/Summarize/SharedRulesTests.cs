using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;
using ScreenTail.Api.Summarize;

namespace ScreenTail.Api.Tests.Summarize;

/// <summary>
/// The validator's patterns and word lists are the ones in <c>research/prompts/shared-rules.json</c>
/// (ST-061, 2026-10-12 review).
///
/// <c>checks.py</c> reads that file; <see cref="DraftRules"/> keeps the same values as constants so the
/// service has no rules file to load. This is what makes "the same rules" a fact rather than a comment:
/// the 2026-10-12 review found the two validators each claiming to mirror the other, and neither doing so.
/// <c>research/tests/test_shared_rules.py</c> holds the Python side to the same file.
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

    /// <summary>Keys whose values are words or data, not patterns.</summary>
    private static readonly HashSet<string> NotPatterns = new(StringComparer.Ordinal)
    {
        "redaction_markers", "connectives", "ignore_case", "walk_steps", "read_growth", "read_slack", "sentence_ends", "value_breaks", "apostrophe_as",
    };

    /// <summary>
    /// A shorthand .NET's Regex and Python's re read differently, or that only one of them has. <c>\s</c>
    /// takes U+001C to U+001F in Python and not here; <c>\w</c> and <c>\b</c> disagree about combining
    /// marks, connector punctuation and other-number characters; <c>\d</c> about digits outside the BMP;
    /// <c>\Z</c> about a final line break; and <c>\p</c> does not exist in Python's re. An escaped
    /// backslash before the letter is not one.
    /// </summary>
    private static readonly Regex Shorthand = new(@"(?<!\\)(?:\\\\)*\\[wWdDsSbBpPzZAG]", RegexOptions.CultureInvariant);

    [Fact]
    public void NoRuleUsesAShorthandTheEnginesReadDifferently()
    {
        foreach (var (name, values) in DraftRules.AsShared().Where(rule => !NotPatterns.Contains(rule.Key)))
        {
            foreach (var pattern in values)
            {
                Assert.False(Shorthand.IsMatch(pattern), $"{name} uses '{Shorthand.Match(pattern).Value}'.");
                Assert.False(Shorthand.IsMatch(DraftRules.Expand(pattern)), $"{name}, written out, uses a shorthand.");
            }
        }
    }

    [Fact]
    public void EveryPatternCompilesWithEveryPlaceholderWrittenOut()
    {
        foreach (var (name, values) in DraftRules.AsShared().Where(rule => !NotPatterns.Contains(rule.Key) && rule.Key != "placeholders"))
        {
            var compiled = DraftRules.Compile(name, values);

            Assert.DoesNotContain("%", compiled.ToString(), StringComparison.Ordinal);
        }
    }

    [Fact]
    public void TheIgnoreCaseListNamesOnlyRulesThatExist()
    {
        var rules = DraftRules.AsShared();

        Assert.All(DraftRules.IgnoreCase, name => Assert.True(rules.ContainsKey(name), $"ignore_case names '{name}', which is not a rule."));
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
            rules[property.Name] = property.Value.ValueKind switch
            {
                JsonValueKind.Array => [.. property.Value.EnumerateArray().Select(item => item.GetString()!)],
                JsonValueKind.Object => [.. property.Value.EnumerateObject().Select(item => $"{item.Name}={item.Value.GetString()}")],
                _ => [property.Value.GetString()!],
            };
        }

        return rules;
    }
}

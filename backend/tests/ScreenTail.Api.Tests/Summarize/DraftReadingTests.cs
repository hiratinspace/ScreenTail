using System.Text;
using ScreenTail.Api.Summarize;

namespace ScreenTail.Api.Tests.Summarize;

/// <summary>
/// How the validator reads a draft before any rule looks at it (ST-061): NFKC from the shared tables, no
/// format characters, every space a plain one, and no runs of them.
///
/// The API cannot use <see cref="string.Normalize()"/>, because with InvariantGlobalization it returns
/// non-ASCII text unchanged. This suite runs with ICU, where it works, so it is the reference the shared
/// tables are checked against here, as <c>unicodedata</c> is in the Python suite.
/// </summary>
public sealed class DraftReadingTests
{
    [Fact]
    public void TheSharedTablesGiveTheSameNfkcAsTheOperatingSystem()
    {
        Assert.True("\uFF49".Normalize(NormalizationForm.FormKC) == "i", "This test needs ICU, and Normalize is not normalising.");

        var wrong = new List<string>();
        foreach (var cp in DraftReading.Assigned)
        {
            if (cp is >= 0xD800 and <= 0xDFFF)
            {
                continue;
            }

            var text = char.ConvertFromUtf32(cp);
            if (DraftReading.Nfkc(text) != text.Normalize(NormalizationForm.FormKC))
            {
                wrong.Add($"U+{cp:X4}");
            }
        }

        Assert.True(wrong.Count == 0, $"{wrong.Count} of the code points Unicode {DraftReading.UnicodeVersion} assigns normalise differently: {string.Join(", ", wrong.Take(10))}");
    }

    [Theory]
    [InlineData("e\u0301", "\u00E9")]
    [InlineData("\u1100\u1161\u11A8", "\uAC01")]
    [InlineData("a\u0323\u0302", "\u1EAD")]
    [InlineData("a\u0302\u0323", "\u1EAD")]
    [InlineData("\uFB01le", "file")]
    [InlineData("\u2460", "1")]
    public void SequencesAreComposedAndReorderedAsNfkcSays(string text, string expected)
    {
        Assert.Equal(expected.Normalize(NormalizationForm.FormKC), text.Normalize(NormalizationForm.FormKC));
        Assert.Equal(expected, DraftValidator.Read(text));
    }

    [Theory]
    [InlineData("In\u200Bstall", "Install")]
    [InlineData("api \u00A0 key", "api key")]
    [InlineData("a\u001Cb\u2028c\td\ne", "a b c d e")]
    [InlineData("\uFF29\uFF4E\uFF53\uFF54\uFF41\uFF4C\uFF4C", "Install")]
    [InlineData("power\u00ADshell\uFEFF", "powershell")]
    public void WhatNobodySeesIsGoneAndEverySpaceIsOne(string text, string expected)
    {
        Assert.Equal(expected, DraftValidator.Read(text));
    }

    [Fact]
    public void ALoneSurrogateIsReadAsTheReplacementCharacter()
    {
        // Not theory data: xunit carries that as UTF-8, which cannot hold a lone surrogate.
        Assert.Equal("\uFFFD lone \uFFFD", DraftValidator.Read("\uD800 lone \uDC00"));
        Assert.Equal("\U0001F600", DraftValidator.Read("\U0001F600"));
    }
}

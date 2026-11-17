using System.Text.RegularExpressions;
using ScreenTrail.Shared.Logging;
using ScreenTrail.Tests.Security;

namespace ScreenTrail.Tests.Logging;

/// <summary>
/// INV-10 at the source: no log message template in the client names a placeholder that would carry
/// content. The scrubber catches a value at run time; this catches the message at review time, which
/// is when "log the title for a minute while I debug this" is written.
/// </summary>
public sealed partial class LogMessagesCarryNoContentTests
{
    [GeneratedRegex("Message\\s*=\\s*\"((?:[^\"\\\\]|\\\\.)*)\"")]
    private static partial Regex Template();

    [GeneratedRegex("\\{(\\w+)")]
    private static partial Regex Placeholder();

    [Theory]
    [InlineData("ScreenTrail.Core")]
    [InlineData("ScreenTrail.Service")]
    [InlineData("ScreenTrail.UI")]
    [InlineData("ScreenTrail.Platform")]
    [InlineData("ScreenTrail.Shared")]
    public void NoLogTemplateNamesAPlaceholderThatCarriesContent(string project)
    {
        var files = ClientSources.Of(project);
        Assert.NotEmpty(files);

        var offenders = new List<string>();
        foreach (var file in files)
        {
            var source = File.ReadAllText(file);
            foreach (Match template in Template().Matches(source))
            {
                foreach (Match placeholder in Placeholder().Matches(template.Groups[1].Value))
                {
                    if (LogScrubber.IsSensitiveKey(placeholder.Groups[1].Value))
                    {
                        offenders.Add($"{Path.GetFileName(file)}: {{{placeholder.Groups[1].Value}}} in \"{template.Groups[1].Value}\"");
                    }
                }
            }
        }

        Assert.Empty(offenders);
    }
}

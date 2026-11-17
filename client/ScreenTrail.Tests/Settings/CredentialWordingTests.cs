using System.Text.RegularExpressions;

namespace ScreenTrail.Tests.Settings;

/// <summary>
/// Integration credentials (the ConnectWise and Hudu site, keys and clientId) are the owner's, changed in
/// the web admin (2026-11-15 security review, ST-082). Settings → Integrations shows them read only, so no
/// text the application shows may tell a technician to enter or fix a credential in Settings. Read off the
/// sources, so a sample line or a new message is caught as well as the ones a test already drives.
/// Choosing a company mapping there is still the technician's, and is not about a credential.
/// </summary>
public sealed partial class CredentialWordingTests
{
    private static readonly string[] Projects = ["ScreenTrail.Core", "ScreenTrail.UI", "ScreenTrail.Service", "ScreenTrail.Shared"];

    [Fact]
    public void NoTextSendsTheTechnicianToSettingsToFixACredential()
    {
        var found = Sources()
            .SelectMany(file => File.ReadLines(file).Select((line, at) => (file, line, at)))
            .Where(l => !IsComment(l.line))
            .SelectMany(l => Literal().Matches(l.line).Select(m => (l.file, l.at, text: m.Groups[1].Value)))
            .Where(l => SendsToSettingsForACredential(l.text))
            .Select(l => $"{Path.GetFileName(l.file)}:{l.at + 1}: {l.text}")
            .ToList();

        Assert.True(found.Count == 0, "Ask the ScreenTrail owner to fix it in the web admin instead:\n" + string.Join('\n', found));
    }

    [Theory]
    [InlineData("Hudu rejected the API key. Update it in Settings → Integrations.")]
    [InlineData("ConnectWise rejected the request: clientId header missing. Add your clientId in Settings, under Integrations.")]
    [InlineData("The stored credential could not be opened. Enter it again in Settings → Integrations.")]
    public void TheCheckWouldCatchTheOldWords(string old) =>
        Assert.True(SendsToSettingsForACredential(old));

    [Theory]
    [InlineData("Map it in Settings → Integrations, then publish the article again.")]
    [InlineData("Fix it now or later in Windows Settings.")]
    [InlineData("Hudu rejected the API key. Ask your ScreenTrail owner to update the Hudu connection in the web admin.")]
    public void TheCheckLeavesMappingsWindowsAndTheOwnersWordsAlone(string text) =>
        Assert.False(SendsToSettingsForACredential(text));

    private static bool SendsToSettingsForACredential(string text) =>
        text.Contains("Settings", StringComparison.Ordinal)
        && !text.Contains("Windows Settings", StringComparison.Ordinal)
        && Credential().IsMatch(text);

    private static bool IsComment(string line)
    {
        var trimmed = line.TrimStart();
        return trimmed.StartsWith("//", StringComparison.Ordinal) || trimmed.StartsWith("<!--", StringComparison.Ordinal);
    }

    private static List<string> Sources()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            var client = Path.Combine(directory.FullName, "client");
            if (Directory.Exists(Path.Combine(client, "ScreenTrail.UI")))
            {
                return Projects
                    .SelectMany(p => Directory.EnumerateFiles(Path.Combine(client, p), "*.*", SearchOption.AllDirectories))
                    .Where(f => f.EndsWith(".cs", StringComparison.Ordinal) || f.EndsWith(".xaml", StringComparison.Ordinal))
                    .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                        && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
                    .ToList();
            }
        }

        throw new InvalidOperationException("The client sources were not found above the test's directory.");
    }

    /// <summary>A double-quoted literal or attribute value on one line.</summary>
    [GeneratedRegex("\"((?:[^\"\\\\\\r\\n]|\\\\.)*)\"")]
    private static partial Regex Literal();

    /// <summary>Words that make a sentence about a credential rather than a company mapping.</summary>
    [GeneratedRegex(@"api key|\bkeys?\b|clientid|credential|site address|public address|connect it|enter it|update it|fix (it|them)", RegexOptions.IgnoreCase)]
    private static partial Regex Credential();
}

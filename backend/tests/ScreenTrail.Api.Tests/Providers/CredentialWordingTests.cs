using System.Text.RegularExpressions;

namespace ScreenTrail.Api.Tests.Providers;

/// <summary>
/// Integration credentials (the ConnectWise and Hudu site, keys and clientId) are the owner's, changed in
/// the web admin (2026-11-15 security review, ST-082). The backend's words reach a technician verbatim on
/// the publish pane, the mapping prompt, Settings, the tray and History, so none may tell them to enter or
/// fix a credential in Settings: they ask the ScreenTrail owner, in the web admin. Read off the sources, so
/// a new provider error is caught as well as the ones a test already drives. Choosing a company mapping in
/// Settings → Integrations is still the technician's, and is not about a credential.
/// </summary>
public sealed partial class CredentialWordingTests
{
    [Fact]
    public void NoBackendTextSendsTheTechnicianToSettingsToFixACredential()
    {
        var found = Sources()
            .SelectMany(file => File.ReadLines(file).Select((line, at) => (file, line, at)))
            .Where(l => !l.line.TrimStart().StartsWith("//", StringComparison.Ordinal))
            .SelectMany(l => Literal().Matches(l.line).Select(m => (l.file, l.at, text: m.Groups[1].Value)))
            .Where(l => SendsToSettingsForACredential(l.text))
            .Select(l => $"{Path.GetFileName(l.file)}:{l.at + 1}: {l.text}")
            .ToList();

        Assert.True(found.Count == 0, "Ask your ScreenTrail owner to fix it in the web admin instead:\n" + string.Join('\n', found));
    }

    [Theory]
    [InlineData("Enter it again in Settings → Integrations.", true)]
    [InlineData("Add your clientId in Settings → Integrations.", true)]
    [InlineData("Map the company in Settings → Integrations.", false)]
    [InlineData("Ask your ScreenTrail owner to update the Hudu connection in the web admin.", false)]
    public void TheCheckTellsCredentialsFromMappings(string text, bool caught) =>
        Assert.Equal(caught, SendsToSettingsForACredential(text));

    private static bool SendsToSettingsForACredential(string text) =>
        text.Contains("Settings", StringComparison.Ordinal) && Credential().IsMatch(text);

    private static List<string> Sources()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            var candidate = Path.Combine(directory.FullName, "backend", "src");
            if (Directory.Exists(candidate))
            {
                return [.. Directory.EnumerateFiles(candidate, "*.cs", SearchOption.AllDirectories)
                    .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                        && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal))];
            }
        }

        throw new InvalidOperationException("backend/src was not found above the test's directory.");
    }

    /// <summary>A double-quoted literal on one line.</summary>
    [GeneratedRegex("\"((?:[^\"\\\\\\r\\n]|\\\\.)*)\"")]
    private static partial Regex Literal();

    /// <summary>Words that make a sentence about a credential rather than a company mapping.</summary>
    [GeneratedRegex(@"api key|\bkeys?\b|clientid|credential|site address|public address|connect it|enter it|update it|fix (it|them)", RegexOptions.IgnoreCase)]
    private static partial Regex Credential();
}

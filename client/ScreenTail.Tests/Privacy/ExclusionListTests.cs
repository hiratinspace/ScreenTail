using ScreenTail.Core.Detection;
using ScreenTail.Core.Privacy;

namespace ScreenTail.Tests.Privacy;

/// <summary>
/// ST-043: the windows that must never be photographed, whatever scope says.
///
/// The case this exists for is not an out-of-scope window — ST-023 already handles those. It is a window
/// that is squarely inside the support session and still must not be captured: a technician opening
/// 1Password to look up the customer's admin password, in the middle of a ScreenConnect session.
/// </summary>
public sealed class ExclusionListTests
{
    private static readonly string DefaultsPath = Path.Combine(
        AppContext.BaseDirectory, "..", "..", "..", "..", "..", "shared", "registry", "exclusions-default.json");

    private static ExclusionList Defaults() => ExclusionList.Load(File.ReadAllText(Path.GetFullPath(DefaultsPath)));

    [Theory]
    [InlineData("1Password")]
    [InlineData("1Password.exe")]
    [InlineData("Bitwarden")]
    [InlineData("KeePassXC")]
    [InlineData("LastPass")]
    [InlineData("Dashlane")]
    [InlineData("Keeper")]
    [InlineData("NordPass")]
    [InlineData("RoboForm")]
    [InlineData("Enpass")]
    [InlineData("CyberArk")]
    [InlineData("SecretServer")]
    public void APasswordManagerIsNeverCaptured(string process)
    {
        // ST-043's first criterion. Windows reports a process without its extension, and a tenant's list
        // may be written either way, so both have to match the same rule.
        Assert.NotNull(Defaults().Match(Window(process: process)));
    }

    [Fact]
    public void TheDefaultsCoverAtLeastTenApplications()
    {
        // ST-043's third criterion, counted by rule rather than by process name — listing four aliases of
        // one product is not ten password managers.
        Assert.True(Defaults().ExcludedApplications >= 10, $"only {Defaults().ExcludedApplications} applications are excluded");
    }

    [Theory]
    [InlineData("Online Banking — Chase")]
    [InlineData("Barclays | Personal Banking")]
    [InlineData("Monzo")]
    [InlineData("Sign in to your account")]
    [InlineData("1Password — Acme Dental vault")]
    [InlineData("Payroll — March 2026")]
    public void ABrowserTabAboutMoneyOrCredentialsIsExcluded(string tab)
    {
        // ST-043's second criterion. The tab title is all we have until UI Automation gives us the URL,
        // and a technician checking a customer's bank portal is doing support work with a scope that
        // allows it.
        Assert.NotNull(Defaults().Match(Window(process: "chrome", browserTab: tab)));
    }

    [Theory]
    [InlineData("Print Spooler Properties")]
    [InlineData("Services")]
    [InlineData("Acme Dental — ticket 4821 — ConnectWise")]
    [InlineData("Device Manager")]
    public void OrdinarySupportWorkIsNotExcluded(string title)
    {
        // The other half of the criterion, and the one that decides whether the feature is usable. An
        // exclusion list that caught everything would produce sessions with no screenshots at all.
        Assert.Null(Defaults().Match(Window(process: "mmc", title: title)));
    }

    [Fact]
    public void AWindowWindowsWillNotIdentifyIsExcluded()
    {
        // No process name and no title means Windows told us nothing about what is in front — a secure
        // desktop, a window we may not open, a process that just died. "We could not find out" is not a
        // reason to photograph something.
        var rule = Defaults().Match(Window(process: null, title: string.Empty));

        Assert.NotNull(rule);
        Assert.Equal("unknown-window", rule!.Id);
    }

    [Fact]
    public void AWindowWeCanNameButNotIdentifyIsJudgedOnItsTitle()
    {
        // A process we could not open but whose title we can read is usually an elevated window. ST-022
        // already reports those; this must not swallow them as "unknown" and lose that distinction.
        Assert.Null(Defaults().Match(Window(process: null, title: "Administrator: Command Prompt")));
        Assert.NotNull(Defaults().Match(Window(process: null, title: "Bitwarden — Vault")));
    }

    [Fact]
    public void APatternThatCannotFinishReadingATitleExcludesIt()
    {
        // The asymmetry that matters. RemoteToolRegistry treats a regex timeout as "not a remote tool",
        // and the cost there is a session that does not start. Here the same answer would mean "capture
        // it", and the cost is a screenshot of somebody's password vault in a ticket.
        var catastrophic = ExclusionList.Load("""
            {
              "version": "test",
              "processes": [],
              "title_patterns": [{ "id": "slow", "display_name": "Slow rule", "pattern": "^(a+)+$" }],
              "url_fragments": []
            }
            """);

        var rule = catastrophic.Match(Window(process: "chrome", browserTab: new string('a', 60) + "!"));

        Assert.NotNull(rule);
        Assert.Equal("slow", rule!.Id);
    }

    [Fact]
    public void AListThatWillNotLoadIsRefusedRatherThanHalfApplied()
    {
        // A list that loaded the rules before the broken one would silently stop excluding everything
        // after it, and nothing would say so. Refusing is loud; half-working is not.
        var broken = """
            {
              "version": "test",
              "processes": [{ "id": "empty", "display_name": "Nothing", "processes": [] }],
              "title_patterns": [{ "id": "bad", "display_name": "Bad", "pattern": "(unclosed" }],
              "url_fragments": []
            }
            """;

        var ex = Assert.Throws<InvalidOperationException>(() => ExclusionList.Load(broken));

        Assert.Contains("empty", ex.Message, StringComparison.Ordinal);
        Assert.Contains("bad", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void TheRuleNamesTheRuleAndNotTheWindow()
    {
        // The name reaches the HUD ("Paused: excluded app — 1Password") and the audit log. A window title
        // there would put a customer's bank or a ticket subject into both (INV-10).
        var rule = Defaults().Match(Window(process: "chrome", browserTab: "Chase — Acme Dental operating account"));

        Assert.NotNull(rule);
        Assert.DoesNotContain("Acme", rule!.DisplayName, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Chase", rule.DisplayName, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TheBrowserTabBeatsTheWindowTitle()
    {
        // A browser's window title is the tab plus the browser's own name, and ST-022 already extracts the
        // tab. Judging the raw title would let "Bitwarden" in a background tab's leftover title decide.
        var list = Defaults();

        Assert.NotNull(list.Match(Window(process: "chrome", browserTab: "Bitwarden", title: "Acme — ConnectWise")));
        Assert.Null(list.Match(Window(process: "chrome", browserTab: "Acme — ConnectWise", title: "Bitwarden")));
    }

    [Fact]
    public void TheDefaultListLoadsAndIsNotEmpty()
    {
        // The file ships with the product. A typo in it is a privacy feature that silently does nothing.
        var list = Defaults();

        Assert.True(list.Rules > 20, $"the default list has only {list.Rules} rules");
        Assert.StartsWith("exclusions-", list.Version, StringComparison.Ordinal);
    }

    private static ForegroundWindowInfo Window(string? process, string title = "", string? browserTab = null) =>
        new(nint.Zero, 42, process, title, "Window", browserTab, false, DateTimeOffset.UnixEpoch);
}

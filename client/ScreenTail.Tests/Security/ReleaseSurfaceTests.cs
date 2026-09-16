using System.Text.RegularExpressions;

namespace ScreenTail.Tests.Security;

/// <summary>
/// ST-012: the client listens on nothing and ships no debug door.
///
/// A grep proves it today; this proves it tomorrow. Both of these are the sort of thing that arrives in a
/// hurry — a diagnostic HTTP endpoint "just for the pilot", a <c>#if DEBUG</c> that skips the client
/// check while someone is testing — and both would be invisible in review of the commit that added them.
///
/// The client's only interface is a named pipe with a per-user ACL, a verified peer and a per-run token
/// (ADR-0003). Anything that listens on a socket is a second interface with none of that, on a
/// technician's machine that sits inside a customer's network all day.
/// </summary>
public sealed class ReleaseSurfaceTests
{
    /// <summary>Types that mean "something can connect to us", as they appear in source.</summary>
    private static readonly string[] Listeners =
    [
        "TcpListener",
        "HttpListener",
        "WebApplication.Create",
        "UseKestrel",
        "UseUrls",
        "SocketAsyncEventArgs",
        "Socket(",
    ];

    private static readonly string[] Projects =
    [
        "ScreenTail.Core",
        "ScreenTail.Service",
        "ScreenTail.Shared",
        "ScreenTail.UI",
        "ScreenTail.Platform",
    ];

    public static TheoryData<string> ClientProjects() => [.. Projects];

    [Theory]
    [MemberData(nameof(ClientProjects))]
    public void NothingInTheClientListensForConnections(string project)
    {
        var offenders = SourceFiles(project)
            .SelectMany(file => Listeners
                .Where(listener => File.ReadAllText(file).Contains(listener, StringComparison.Ordinal))
                .Select(listener => $"{Path.GetFileName(file)}: {listener}"))
            .ToList();

        Assert.True(
            offenders.Count == 0,
            $"The client's only interface is the named pipe (ADR-0003). Found: {string.Join(", ", offenders)}");
    }

    [Theory]
    [MemberData(nameof(ClientProjects))]
    public void NoCodeIsCompiledOutOfReleaseBuilds(string project)
    {
        // A release build that differs from what was tested is a release build nobody tested. The
        // dangerous shape is not #if DEBUG around a log line; it is #if DEBUG around a check, which makes
        // the development build the permissive one and hides that in the diff.
        var conditional = new Regex(@"^\s*#\s*(if|elif)\s+.*\b(DEBUG|TRACE)\b", RegexOptions.Multiline);

        var offenders = SourceFiles(project)
            .Where(file => conditional.IsMatch(File.ReadAllText(file)))
            .Select(Path.GetFileName)
            .ToList();

        Assert.True(
            offenders.Count == 0,
            $"Release and debug builds must contain the same code. Found conditionals in: {string.Join(", ", offenders)}");
    }

    [Fact]
    public void TheUiProcessCannotReachTheCaptureEngine()
    {
        // ST-085's architecture rule, and the reason the UI is safe to leave running all day. The UI holds
        // no store, no hooks and no OCR: everything it knows arrives over the pipe, so INV-1's read-path
        // filtering has one owner rather than one per window. A project reference is how that would be
        // undone, and it would look like a convenience in the diff.
        var project = ProjectFile("ScreenTail.UI");
        var text = File.ReadAllText(project);

        Assert.DoesNotContain("ScreenTail.Service", text, StringComparison.Ordinal);
    }

    [Fact]
    public void EveryHttpClientInTheClientIsBuiltThroughTheEgressGuard()
    {
        // INV-8 says local-only mode is enforced by an allow-list, "not by convention". EgressGuard's own
        // comment called itself "the handler every HttpClient in the client is built with" while nothing
        // constructed one anywhere (weaknesses P1-1), which is precisely the convention it was written to
        // replace. The first bare client is the one that gets it wrong, so there are none.
        var offenders = Projects
            .SelectMany(SourceFiles)
            .Where(file => !file.EndsWith("EgressGuard.cs", StringComparison.Ordinal))
            .Where(file => BareHttpClient.IsMatch(File.ReadAllText(file)))
            .Select(Path.GetFileName)
            .ToList();

        Assert.True(
            offenders.Count == 0,
            $"Build HttpClient through EgressGuard so INV-8 is enforced rather than remembered. Found in: {string.Join(", ", offenders)}");
    }

    [Fact]
    public void TheDiagnosticsPanelsSampleIsOnlyForTheRenderHarness()
    {
        // The panel is what a technician turns the screen round and shows a customer. It rendered a
        // literal in production — "local-only: yes" from a constant — which is worse than no panel at all
        // (weaknesses P1-2). The sample stays for the CI render; nothing that runs may reach it.
        var offenders = SourceFiles("ScreenTail.UI")
            .Where(file => !Path.GetFileName(file).StartsWith("DiagnosticsWindow", StringComparison.Ordinal))
            .Where(file => File.ReadAllText(file).Contains("DiagnosticsWindow.Sample", StringComparison.Ordinal))
            .Select(Path.GetFileName)
            .ToList();

        Assert.True(
            offenders.Count == 0,
            $"The diagnostics panel shows what the service said, never a sample. Found in: {string.Join(", ", offenders)}");
    }

    [Fact]
    public void TheseTestsAreLookingAtRealFiles()
    {
        // The whole suite above passes trivially if the glob finds nothing — which is what happens the
        // first time somebody runs it from a different working directory.
        foreach (var project in Projects)
        {
            Assert.True(SourceFiles(project).Count > 0, $"No sources found for {project}.");
        }
    }

    /// <summary>
    /// <c>new HttpClient(</c> with no handler, or <c>new HttpClient()</c>. A client built around the
    /// guard passes one in, which is the whole distinction.
    /// </summary>
    private static readonly Regex BareHttpClient = new(@"new\s+HttpClient\s*\(\s*\)", RegexOptions.Compiled);

    private static string ProjectFile(string project)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, "client", project, project + ".csproj");
            if (File.Exists(candidate))
            {
                return candidate;
            }

            directory = directory.Parent;
        }

        throw new FileNotFoundException($"No project file for {project}.");
    }

    private static IReadOnlyList<string> SourceFiles(string project)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, "client", project);
            if (Directory.Exists(candidate))
            {
                return
                [
                    .. Directory.EnumerateFiles(candidate, "*.cs", SearchOption.AllDirectories)
                        .Where(file => !file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
                        .Where(file => !file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal)),
                ];
            }

            directory = directory.Parent;
        }

        return [];
    }
}

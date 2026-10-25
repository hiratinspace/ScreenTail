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
        //
        // 2026-10-12 review (T-120): "bare" used to mean `new HttpClient()` and nothing else, so a client
        // built around a SocketsHttpHandler, a raw web socket or the old WebRequest family bypassed the
        // guard with the test still green. The handler, socket and request types are named instead:
        // EgressGuard.cs is the one file allowed to build the handler underneath.
        var offenders = Projects
            .SelectMany(SourceFiles)
            .Where(file => !file.EndsWith("EgressGuard.cs", StringComparison.Ordinal))
            .Where(file => BuildsUnguardedHttp(File.ReadAllText(file)))
            .Select(Path.GetFileName)
            .ToList();

        Assert.True(
            offenders.Count == 0,
            $"Build HttpClient through EgressGuard so INV-8 is enforced rather than remembered. Found in: {string.Join(", ", offenders)}");
    }

    [Theory]
    [InlineData("var c = new HttpClient();", true)]
    [InlineData("var c = new HttpClient( );", true)]
    [InlineData("var c = new HttpClient(new SocketsHttpHandler());", true)]
    [InlineData("var h = new HttpClientHandler { AllowAutoRedirect = false };", true)]
    [InlineData("var h = new WinHttpHandler();", true)]
    [InlineData("using var socket = new ClientWebSocket();", true)]
    [InlineData("var r = WebRequest.Create(uri);", true)]
    [InlineData("var r = (HttpWebRequest)WebRequest.Create(uri);", true)]
    [InlineData("using var w = new WebClient();", true)]
    [InlineData("services.AddHttpClient<Foo>();", true)]
    [InlineData("var c = new HttpClient(egress) { BaseAddress = backend };", false)]
    [InlineData("var c = new HttpClient(new EgressGuard(new EgressPolicy()));", false)]
    [InlineData("// a comment that mentions SocketsHttpHandler is not a handler", false)]
    [InlineData("/// <summary>Not a WebClient either.</summary>", false)]
    [InlineData("HttpClientHandlerSettings x;", false)]
    [InlineData("var request = new HttpRequestMessage(HttpMethod.Get, uri);", false)]
    public void TheUnguardedHttpCheckKnowsTheShapesItIsFor(string snippet, bool offends)
    {
        // The check above is only as good as this list. A self-test over snippets is what stops a regex
        // edit from quietly matching nothing.
        Assert.Equal(offends, BuildsUnguardedHttp(snippet));
    }

    [Fact]
    public void NothingThatRunsBuildsASampleWindow()
    {
        // The diagnostics panel is what a technician turns the screen round and shows a customer. It
        // rendered a literal in production ("local-only: yes" from a constant), which is worse than no
        // panel at all (weaknesses P1-2). The sample stays for the CI render; nothing that runs may reach
        // it.
        //
        // 2026-10-12 review (T-149): the old check looked for `DiagnosticsWindow.Sample` and missed the
        // parameterless DiagnosticsWindow() and ShellWindow() constructors, which build the same sample
        // and are what a hurried `new ShellWindow()` in the tray would call. Each window may build its
        // own sample; nothing else may ask for one.
        var offenders = SourceFiles("ScreenTail.UI")
            .Where(file => Path.GetFileName(file) is not ("DiagnosticsWindow.xaml.cs" or "ShellWindow.xaml.cs"))
            .SelectMany(file => SampleWindow.Matches(File.ReadAllText(file))
                .Select(match => $"{Path.GetFileName(file)}: {match.Value}"))
            .ToList();

        Assert.True(
            offenders.Count == 0,
            $"The shell and the diagnostics panel show what the service said, never a sample. Found: {string.Join(", ", offenders)}");

        // And WPF's own way of building a window nobody constructed in code.
        var app = Path.Combine(Path.GetDirectoryName(ProjectFile("ScreenTail.UI"))!, "App.xaml");
        Assert.DoesNotContain("StartupUri", File.ReadAllText(app), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("var w = new DiagnosticsWindow();", true)]
    [InlineData("var w = new ShellWindow();", true)]
    [InlineData("var w = new ShellWindow( );", true)]
    [InlineData("var w = new ShellWindow((string?)null);", true)]
    [InlineData("var w = new ShellWindow(null);", true)]
    [InlineData("var d = DiagnosticsWindow.Sample;", true)]
    [InlineData("var w = new ScreenTail.UI.Shell.ShellWindow();", true)]
    [InlineData("var w = new global::ScreenTail.UI.Diagnostics.DiagnosticsWindow();", true)]
    [InlineData("var w = new ShellWindow(state, loadReview, history, settings);", false)]
    [InlineData("var w = new DiagnosticsWindow(From(reported));", false)]
    [InlineData("var w = new ShellWindow(directory);", false)]
    public void TheSampleWindowCheckKnowsTheShapesItIsFor(string snippet, bool offends)
    {
        Assert.Equal(offends, SampleWindow.IsMatch(snippet));
    }

    [Fact]
    public void TheClientNeverNamesAConcreteProvider()
    {
        // ST-090's third criterion. The client asks the backend to publish and never learns which PSA
        // that means. A client that knew about ConnectWise would need a ConnectWise release to support
        // HaloPSA, and — worse — would need a second set of a customer's PSA credentials sitting on a
        // technician's laptop.
        //
        // The provider *names* may appear in copy: Settings has to say which platform is connected.
        // What may not appear is a provider type, because that is a dependency rather than a word.
        var types = new[] { "IPsaProvider", "IDocProvider", "ProviderResult", "ConnectWiseClient", "HuduClient" };
        var offenders = Projects
            .SelectMany(SourceFiles)
            .SelectMany(file => types
                .Where(type => File.ReadAllText(file).Contains(type, StringComparison.Ordinal))
                .Select(type => $"{Path.GetFileName(file)}: {type}"))
            .ToList();

        Assert.True(
            offenders.Count == 0,
            $"The client publishes through the backend and knows no provider by type. Found: {string.Join(", ", offenders)}");
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
    /// <c>new HttpClient()</c> with no handler: a client built around the guard passes one in, which is
    /// the whole distinction.
    /// </summary>
    private static readonly Regex BareHttpClient = new(@"new\s+HttpClient\s*\(\s*\)", RegexOptions.Compiled);

    /// <summary>
    /// The types that make a request without going through a handler somebody chose: the handlers an
    /// <c>HttpClient</c> can be built around, the raw socket, the old request family, and the factory
    /// that builds clients behind a caller's back. Whole words, so a type that merely contains one of
    /// these names is not an offender.
    /// </summary>
    private static readonly Regex UnguardedHttpType = new(
        @"\b(SocketsHttpHandler|HttpClientHandler|WinHttpHandler|HttpMessageInvoker|ClientWebSocket|HttpWebRequest|WebRequest|WebClient|IHttpClientFactory|AddHttpClient)\b",
        RegexOptions.Compiled);

    /// <summary>Line comments, so a comment that names a type is not read as code that uses it.</summary>
    private static readonly Regex LineComment = new(@"//.*$", RegexOptions.Compiled | RegexOptions.Multiline);

    /// <summary>
    /// The sample constructors and the sample itself: <c>new DiagnosticsWindow()</c>, <c>new ShellWindow()</c>,
    /// a ShellWindow built on a null screenshot directory, and <c>DiagnosticsWindow.Sample</c>, with or
    /// without the namespace in front.
    /// </summary>
    private static readonly Regex SampleWindow = new(
        @"new\s+(?:global::)?(?:[\w.]+\.)?(?:DiagnosticsWindow|ShellWindow)\s*\(\s*(?:\(\s*string\??\s*\)\s*)?(?:null\s*)?\)|DiagnosticsWindow\.Sample\b",
        RegexOptions.Compiled);

    private static bool BuildsUnguardedHttp(string source)
    {
        var code = LineComment.Replace(source, string.Empty);
        return BareHttpClient.IsMatch(code) || UnguardedHttpType.IsMatch(code);
    }

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

    private static IReadOnlyList<string> SourceFiles(string project) => ClientSources.Of(project);
}

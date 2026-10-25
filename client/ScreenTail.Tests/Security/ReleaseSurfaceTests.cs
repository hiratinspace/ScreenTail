using System.Text;
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
        // EgressGuard.cs is the one file allowed to build the handler underneath. A client built without
        // a handler in any spelling (target-typed, with an initializer, with its namespace) is bare too,
        // and so is a class of our own derived from HttpClient. Comments and string literals are taken
        // out first, so neither a URL nor a message that names a type decides the answer.
        //
        // The product projects only. The test projects never run on a technician's machine, and they
        // build clients over fake handlers on purpose; that is what a test of the guard looks like.
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
    [InlineData("HttpClient c = new();", true)]
    [InlineData("using HttpClient c = new( );", true)]
    [InlineData("private readonly HttpClient _http = new();", true)]
    [InlineData("public HttpClient Http { get; } = new();", true)]
    [InlineData("private static HttpClient Make() => new();", true)]
    [InlineData("var c = new HttpClient { BaseAddress = b };", true)]
    [InlineData("var c = new HttpClient\n{\n    BaseAddress = b,\n};", true)]
    [InlineData("var c = new System.Net.Http.HttpClient();", true)]
    [InlineData("var c = new global::System.Net.Http.HttpClient();", true)]
    [InlineData("sealed class Raw : HttpClient { }", true)]
    [InlineData("internal sealed class Raw(HttpMessageHandler h) : System.Net.Http.HttpClient(h) { }", true)]
    [InlineData("using var tcp = new TcpClient(host, 443);", true)]
    [InlineData("using var udp = new UdpClient();", true)]
    [InlineData("var s = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);", true)]
    [InlineData("var s = new System.Net.Sockets.Socket(a, b, c);", true)]
    [InlineData("using var tls = new SslStream(stream);", true)]
    [InlineData("await using var q = await QuicConnection.ConnectAsync(options);", true)]
    [InlineData("var u = new Uri(\"https://x\"); var h = new SocketsHttpHandler();", true)]
    [InlineData("var u = @\"C:\\temp\\\"; var h = new SocketsHttpHandler();", true)]
    [InlineData("var q = $\"{(a ? \"x\" : \"//y\")}\"; var h = new SocketsHttpHandler();", true)]
    [InlineData("char c = '\"'; var h = new SocketsHttpHandler();", true)]
    [InlineData("var raw = \"\"\"a \"quoted\" //thing\"\"\"; var h = new SocketsHttpHandler();", true)]
    [InlineData("var c = new HttpClient(egress) { BaseAddress = backend };", false)]
    [InlineData("var c = new HttpClient(new EgressGuard(new EgressPolicy()));", false)]
    [InlineData("// a comment that mentions SocketsHttpHandler is not a handler", false)]
    [InlineData("/// <summary>Not a WebClient either.</summary>", false)]
    [InlineData("HttpClientHandlerSettings x;", false)]
    [InlineData("var request = new HttpRequestMessage(HttpMethod.Get, uri);", false)]
    [InlineData("HttpClient c = new(egress);", false)]
    [InlineData("HttpClient? http = null;", false)]
    [InlineData("public HttpClient Client(HttpMessageHandler handler) => new(handler);", false)]
    [InlineData("internal sealed class Sender(HttpClient http) : ISender { }", false)]
    [InlineData("var s = \"SocketsHttpHandler is named in this message\";", false)]
    [InlineData("/* new HttpClient() in a block comment */", false)]
    [InlineData("var url = \"https://x\"; // new TcpClient()", false)]
    [InlineData("var p = @\"C:\\path\\\"; var c = new HttpClient(guard);", false)]
    [InlineData("var raw = \"\"\"a \"quoted\" new HttpClient()\"\"\"; var c = new HttpClient(guard);", false)]
    [InlineData("var q = $\"{{new HttpClient()}}\";", false)]
    [InlineData("var listener = new TcpClientOptions();", false)]
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
        //
        // Any spelling counts: target-typed, with the namespace, with a null, a null! or a default for the
        // screenshot directory. The one-argument shell constructor is the render harness's, so App.xaml.cs
        // is the one file that may call it, behind its --render switch.
        var offenders = SourceFiles("ScreenTail.UI")
            .Where(file => Path.GetFileName(file) is not ("DiagnosticsWindow.xaml.cs" or "ShellWindow.xaml.cs"))
            .SelectMany(file => SampleWindows(File.ReadAllText(file), inApp: Path.GetFileName(file) == "App.xaml.cs")
                .Select(found => $"{Path.GetFileName(file)}: {found}"))
            .ToList();

        Assert.True(
            offenders.Count == 0,
            $"The shell and the diagnostics panel show what the service said, never a sample. Found: {string.Join(", ", offenders)}");

        // And WPF's own way of building a window nobody constructed in code.
        var app = Path.Combine(Path.GetDirectoryName(ProjectFile("ScreenTail.UI"))!, "App.xaml");
        Assert.DoesNotContain("StartupUri", File.ReadAllText(app), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("var w = new DiagnosticsWindow();", false, true)]
    [InlineData("var w = new ShellWindow();", false, true)]
    [InlineData("var w = new ShellWindow( );", true, true)]
    [InlineData("var w = new ShellWindow((string?)null);", false, true)]
    [InlineData("var w = new ShellWindow(null);", false, true)]
    [InlineData("var w = new ShellWindow(null!);", false, true)]
    [InlineData("var w = new ShellWindow(default);", false, true)]
    [InlineData("var w = new ShellWindow(directory);", false, true)]
    [InlineData("ShellWindow w = new();", false, true)]
    [InlineData("ShellWindow w = new(directory);", false, true)]
    [InlineData("DiagnosticsWindow w = new();", false, true)]
    [InlineData("var d = DiagnosticsWindow.Sample;", false, true)]
    [InlineData("var w = new ScreenTail.UI.Shell.ShellWindow();", false, true)]
    [InlineData("var w = new global::ScreenTail.UI.Diagnostics.DiagnosticsWindow();", false, true)]
    [InlineData("window = new ShellWindow(directory);", true, false)]
    [InlineData("var w = new ShellWindow(state, loadReview, history, settings);", false, false)]
    [InlineData("_window ??= new ShellWindow(\n    _state,\n    LoadReviewAsync,\n    () => new HistoryViewModel(_state, ListSessionsAsync),\n    () => new SettingsViewModel(a, b));", false, false)]
    [InlineData("var w = new DiagnosticsWindow(From(reported));", false, false)]
    [InlineData("var w = new DiagnosticsWindow(From(a, b));", false, false)]
    [InlineData("var s = \"new ShellWindow() in a message\"; // new ShellWindow()", false, false)]
    public void TheSampleWindowCheckKnowsTheShapesItIsFor(string snippet, bool inApp, bool offends)
    {
        Assert.Equal(offends, SampleWindows(snippet, inApp).Count > 0);
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
    /// An <c>HttpClient</c> built with no handler, in every spelling: <c>new HttpClient()</c>, with an
    /// initializer and no arguments, with its namespace, and target-typed <c>new()</c> where the declared
    /// type is HttpClient. A client built around the guard passes one in, which is the whole distinction.
    /// And a class of our own derived from HttpClient, which can build its own handler underneath.
    /// </summary>
    private static readonly Regex BareHttpClient = new(
        @"new\s+(?:global::)?(?:System\.Net\.Http\.)?HttpClient\s*(?:\(\s*\)|\{)"
        + @"|\bHttpClient\??\s+@?\w+\s*(?:\{[^{}]*\}\s*)?=\s*new\s*\(\s*\)"
        + @"|\bHttpClient\??\s+@?\w+\s*\([^()]*\)\s*=>\s*new\s*\(\s*\)"
        + @"|\b(?:class|record)\s+[^:{;]*:\s*(?:global::)?(?:[\w.]+\.)?HttpClient\b",
        RegexOptions.Compiled);

    /// <summary>A raw socket, by constructor, with or without its namespace.</summary>
    private static readonly Regex RawSocket = new(@"\bnew\s+(?:global::)?(?:[\w.]+\.)?Socket\s*\(", RegexOptions.Compiled);

    /// <summary>
    /// The types that make a request without going through a handler somebody chose: the handlers an
    /// <c>HttpClient</c> can be built around, the raw socket and its TCP, UDP, TLS and QUIC wrappers, the
    /// old request family, and the factory that builds clients behind a caller's back. Whole words, so a
    /// type that merely contains one of these names is not an offender.
    /// </summary>
    private static readonly Regex UnguardedHttpType = new(
        @"\b(SocketsHttpHandler|HttpClientHandler|WinHttpHandler|HttpMessageInvoker|ClientWebSocket|HttpWebRequest|WebRequest|WebClient|IHttpClientFactory|AddHttpClient|TcpClient|UdpClient|SslStream|QuicConnection)\b",
        RegexOptions.Compiled);

    /// <summary>A window constructor by name, with or without its namespace, up to its opening parenthesis.</summary>
    private static readonly Regex WindowConstructor = new(
        @"new\s+(?:global::)?(?:[\w.]+\.)?(?<window>DiagnosticsWindow|ShellWindow)\s*\(",
        RegexOptions.Compiled);

    /// <summary>A window declared by type and built target-typed: <c>ShellWindow w = new(</c>.</summary>
    private static readonly Regex TargetTypedWindow = new(
        @"\b(?<window>DiagnosticsWindow|ShellWindow)\??\s+@?\w+\s*=\s*new\s*\(",
        RegexOptions.Compiled);

    private static readonly Regex DiagnosticsSample = new(@"\bDiagnosticsWindow\.Sample\b", RegexOptions.Compiled);

    private static bool BuildsUnguardedHttp(string source)
    {
        var code = CodeOnly(source);
        return BareHttpClient.IsMatch(code) || UnguardedHttpType.IsMatch(code) || RawSocket.IsMatch(code);
    }

    /// <summary>
    /// What would build a sample window: a window constructor with no arguments, the sample itself, or
    /// the one-argument shell constructor (the render harness's) anywhere but <paramref name="inApp"/>.
    /// The four-argument shell and the one-argument diagnostics window are the real ones.
    /// </summary>
    private static List<string> SampleWindows(string source, bool inApp)
    {
        var code = CodeOnly(source);
        var found = new List<string>();
        foreach (var match in WindowConstructor.Matches(code).Concat(TargetTypedWindow.Matches(code)))
        {
            var arguments = TopLevelArguments(code, match.Index + match.Length);
            var shell = match.Groups["window"].Value == "ShellWindow";
            if (arguments == 0 || (shell && arguments == 1 && !inApp))
            {
                found.Add(match.Value);
            }
        }

        found.AddRange(DiagnosticsSample.Matches(code).Select(m => m.Value));
        return found;
    }

    /// <summary>How many arguments the call whose opening parenthesis ends at <paramref name="from"/> has.</summary>
    private static int TopLevelArguments(string code, int from)
    {
        var depth = 0;
        var commas = 0;
        var any = false;
        for (var i = from; i < code.Length; i++)
        {
            var c = code[i];
            if (c is '(' or '[' or '{')
            {
                depth++;
            }
            else if (c is ')' or ']' or '}')
            {
                if (depth == 0)
                {
                    return any ? commas + 1 : 0;
                }

                depth--;
            }
            else if (c == ',' && depth == 0)
            {
                commas++;
            }

            if (!char.IsWhiteSpace(c))
            {
                any = true;
            }
        }

        return any ? commas + 1 : 0;
    }

    /// <summary>
    /// The source with comments removed and every string and character literal emptied, so what is
    /// left is code. String-aware: a <c>//</c> inside a URL is not a comment, a quote inside a character
    /// literal does not start a string, and verbatim, raw and interpolated strings end where C# says.
    /// </summary>
    internal static string CodeOnly(string source)
    {
        ArgumentNullException.ThrowIfNull(source);
        var code = new StringBuilder(source.Length);
        var i = 0;
        while (i < source.Length)
        {
            var c = source[i];
            var next = i + 1 < source.Length ? source[i + 1] : '\0';
            if (c == '/' && next == '/')
            {
                while (i < source.Length && source[i] != '\n')
                {
                    i++;
                }

                continue;
            }

            if (c == '/' && next == '*')
            {
                var end = source.IndexOf("*/", i + 2, StringComparison.Ordinal);
                i = end < 0 ? source.Length : end + 2;
                _ = code.Append(' ');
                continue;
            }

            if (c == '\'')
            {
                var j = i + 1;
                while (j < source.Length && source[j] is not '\'' and not '\n')
                {
                    j += source[j] == '\\' ? 2 : 1;
                }

                i = Math.Min(j + 1, source.Length);
                _ = code.Append("' '");
                continue;
            }

            if (c == '"' || (c is '@' or '$' && StartsString(source, i)))
            {
                i = SkipString(source, i);
                _ = code.Append("\"\"");
                continue;
            }

            _ = code.Append(c);
            i++;
        }

        return code.ToString();
    }

    private static bool StartsString(string source, int i)
    {
        while (i < source.Length && source[i] is '@' or '$')
        {
            i++;
        }

        return i < source.Length && source[i] == '"';
    }

    /// <summary>The index just past the string literal starting at <paramref name="start"/>.</summary>
    private static int SkipString(string source, int start)
    {
        var i = start;
        var verbatim = false;
        var interpolated = false;
        while (source[i] is '@' or '$')
        {
            verbatim |= source[i] == '@';
            interpolated |= source[i] == '$';
            i++;
        }

        var quotes = 0;
        while (i + quotes < source.Length && source[i + quotes] == '"')
        {
            quotes++;
        }

        if (quotes >= 3)
        {
            // A raw string ends at the first run of at least as many quotes.
            var fence = new string('"', quotes);
            var end = source.IndexOf(fence, i + quotes, StringComparison.Ordinal);
            if (end < 0)
            {
                return source.Length;
            }

            end += quotes;
            while (end < source.Length && source[end] == '"')
            {
                end++;
            }

            return end;
        }

        if (quotes == 2)
        {
            return i + 2;
        }

        var j = i + 1;
        var depth = 0;
        while (j < source.Length)
        {
            var c = source[j];
            if (depth > 0)
            {
                // Inside an interpolation hole: code, which may hold strings of its own.
                if (c == '"' || (c is '@' or '$' && StartsString(source, j)))
                {
                    j = SkipString(source, j);
                    continue;
                }

                depth += c == '{' ? 1 : c == '}' ? -1 : 0;
                j++;
                continue;
            }

            if (interpolated && c == '{')
            {
                if (j + 1 < source.Length && source[j + 1] == '{')
                {
                    j += 2;
                    continue;
                }

                depth = 1;
                j++;
                continue;
            }

            if (verbatim)
            {
                if (c == '"')
                {
                    if (j + 1 < source.Length && source[j + 1] == '"')
                    {
                        j += 2;
                        continue;
                    }

                    return j + 1;
                }
            }
            else
            {
                if (c == '\\')
                {
                    j += 2;
                    continue;
                }

                if (c is '"' or '\n')
                {
                    return j + 1;
                }
            }

            j++;
        }

        return source.Length;
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

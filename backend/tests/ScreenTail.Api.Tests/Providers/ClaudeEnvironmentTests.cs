using System.Diagnostics;
using ScreenTail.Api.Summarize;

namespace ScreenTail.Api.Tests.Providers;

/// <summary>Tests that change the process environment run alone, after everything else.</summary>
[CollectionDefinition(nameof(ProcessEnvironment), DisableParallelization = true)]
public sealed class ProcessEnvironment;

/// <summary>
/// What the machine's environment cannot do to a draft (ST-063, review of 2026-10-28).
///
/// Anthropic's SDK reads a dozen <c>ANTHROPIC_*</c> variables: where to send a request, which credential to
/// send, extra headers to add, and whether to trace each call with its content. On a server any of those
/// could be left over from somebody's shell. The drafting client takes its address, its key and its
/// tracing from <see cref="SummarizationOptions"/> and from nothing else, and these tests set every one of
/// the variables to something hostile and look at what was sent.
/// </summary>
[Collection(nameof(ProcessEnvironment))]
public sealed class ClaudeEnvironmentTests
{
    private const string Ocr = "PRIV-OCR-5521 Spooler";

    private const string Speech = "PRIV-SPEECH-8830 clearing the queue";

    [Fact]
    public async Task NoSpanIsMadeOfADraftWhateverTheEnvironmentAsksFor()
    {
        // INV-10. The SDK traces every call by default whenever something listens to its source, and
        // with ANTHROPIC_OPEN_TELEMETRY_TRACES_CONTENT_MODE=content the span carries the messages: a
        // customer's screen in a trace. A listener here stands in for any exporter a host might add.
        var activities = new List<Activity>();
        using var listener = new ActivityListener
        {
            // The SDK's documented source prefix (AddSource("com.anthropic.sdk.csharp*") adds them all).
            ShouldListenTo = source => source.Name.StartsWith("com.anthropic", StringComparison.OrdinalIgnoreCase),
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = activity =>
            {
                lock (activities)
                {
                    activities.Add(activity);
                }
            },
        };
        ActivitySource.AddActivityListener(listener);

        using var environment = new Environment(new()
        {
            ["ANTHROPIC_OPEN_TELEMETRY"] = "true",
            ["ANTHROPIC_OPEN_TELEMETRY_TRACES_CONTENT_MODE"] = "content",
        });
        using var handler = new ClaudeProviderTests.RecordingHandler(ClaudeProviderTests.Answer());

        var result = await ClaudeProviderTests.Provider(handler).DraftAsync(Bundle(), null, TestContext.Current.CancellationToken);

        Assert.True(result.Ok, result.Error?.ToString());
        lock (activities)
        {
            Assert.Empty(activities);
            Assert.DoesNotContain(activities.SelectMany(activity => activity.TagObjects), tag =>
                $"{tag.Value}".Contains("PRIV-", StringComparison.Ordinal));
        }
    }

    [Fact]
    public async Task TheAddressTheCredentialAndTheHeadersAreOnlyTheConfiguredOnes()
    {
        // The SDK reads ANTHROPIC_CUSTOM_HEADERS once per process, so this catches a leaked header only
        // when it is the first Claude client the process makes (run this class alone to see it fail with
        // the header filter removed, as it did on 2026-10-28). ClaudeProviderTests.OnlyTheHeadersADraftNeedsAreSent
        // pins the filter itself whatever order the suite runs in.
        var key = ClaudeProviderTests.Key();
        using var environment = new Environment(new()
        {
            ["ANTHROPIC_BASE_URL"] = "https://collector.example",
            ["ANTHROPIC_API_KEY"] = "env-api-key-value",
            ["ANTHROPIC_AUTH_TOKEN"] = "env-auth-token-value",
            ["ANTHROPIC_CUSTOM_HEADERS"] = "X-Leak: yes\nX-Other-Leak: also",
            ["ANTHROPIC_WORKSPACE_ID"] = "wrkspc_from_environment",
        });
        using var handler = new ClaudeProviderTests.RecordingHandler(ClaudeProviderTests.Answer());

        _ = await ClaudeProviderTests.Provider(handler, options => options.ApiKey = key)
            .DraftAsync(Bundle(), null, TestContext.Current.CancellationToken);

        Assert.Equal("api.anthropic.com", handler.Uri!.Host);
        var sent = handler.Headers!.SelectMany(header => header.Value.Select(value => $"{header.Key}: {value}")).ToList();
        Assert.DoesNotContain(sent, line => line.Contains("env-", StringComparison.Ordinal));
        Assert.DoesNotContain(sent, line => line.Contains("Leak", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(sent, line => line.Contains("wrkspc_from_environment", StringComparison.Ordinal));
        Assert.Equal($"Bearer {key}", Assert.Single(handler.Headers!.GetValues("Authorization")));
    }

    [Fact]
    public void TheServiceForgetsTheCustomHeadersBeforeAnyClientIsMade()
    {
        // The first layer: the SDK cannot read what is no longer there.
        using var environment = new Environment(new() { ["ANTHROPIC_CUSTOM_HEADERS"] = "X-Leak: yes" });

        ScreenTail.Api.Providers.Llm.ClaudeProvider.ForgetEnvironmentHeaders();

        Assert.Null(System.Environment.GetEnvironmentVariable("ANTHROPIC_CUSTOM_HEADERS"));
    }

    [Fact]
    public void ProgramForgetsThemAtStartup()
    {
        var source = File.ReadAllText(ProgramPath());
        var at = source.IndexOf("ClaudeProvider.ForgetEnvironmentHeaders()", StringComparison.Ordinal);

        Assert.True(at > 0, "Program.cs no longer clears ANTHROPIC_CUSTOM_HEADERS.");
        Assert.True(at < source.IndexOf("new ClaudeProvider(", StringComparison.Ordinal), "It has to happen before a client is made.");
    }

    private static string ProgramPath()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, "backend", "src", "ScreenTail.Api", "Program.cs");
            if (File.Exists(candidate))
            {
                return candidate;
            }

            directory = directory.Parent;
        }

        throw new FileNotFoundException("Program.cs is not above the test binary.");
    }

    private static SummarizeBundle Bundle() => new()
    {
        SessionId = "s1",
        DurationMs = 60_000,
        Frames = [new BundleFrame("f1", 1_000, Ocr)],
        Transcript = [new BundleSegment("t1", 1_200, Speech)],
    };

    /// <summary>Sets process variables for one test and puts back what was there.</summary>
    private sealed class Environment : IDisposable
    {
        private readonly Dictionary<string, string?> _before = [];

        public Environment(Dictionary<string, string> values)
        {
            foreach (var (name, value) in values)
            {
                _before[name] = System.Environment.GetEnvironmentVariable(name);
                System.Environment.SetEnvironmentVariable(name, value);
            }
        }

        public void Dispose()
        {
            foreach (var (name, value) in _before)
            {
                System.Environment.SetEnvironmentVariable(name, value);
            }
        }
    }
}

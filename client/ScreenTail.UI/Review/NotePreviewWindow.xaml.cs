using System.IO;
using System.Windows;
using ScreenTail.Shared.Schema;
using ScreenTail.UI.Render;
using ScreenTail.UI.Theme;

namespace ScreenTail.UI.Review;

/// <summary>
/// Renders the note pane against a real fixture, in every state, in every theme (ST-074).
///
/// The AC says the pane renders a fixture with no backend, so the preview loads one from
/// <c>research/fixtures/handcrafted/</c> rather than from a sample written next to the harness. A sample
/// invented here would agree with the code by construction and prove nothing; the fixture is the same
/// document the drafting prompt and its checks are evaluated against, and it goes through
/// <see cref="SessionJson"/>, so a pane that cannot render what the product actually produces fails here.
/// </summary>
public partial class NotePreviewWindow : Window
{
    private const string FixturePath = "research/fixtures/handcrafted/spooler-stopped-screenconnect/session.json";

    private readonly string? _screenshotDirectory;

    public NotePreviewWindow()
        : this(null)
    {
    }

    public NotePreviewWindow(string? screenshotDirectory)
    {
        // Before any binding runs, so a property that does not exist is collected rather than merely
        // leaving a label blank in eighteen otherwise convincing screenshots.
        if (screenshotDirectory is not null)
        {
            BindingErrors.Listen();
        }

        InitializeComponent();
        _screenshotDirectory = screenshotDirectory;
        Pane.DataContext = new NoteEditorViewModel(LoadFixture());
        if (_screenshotDirectory is not null)
        {
            ContentRendered += async (_, _) => await CaptureAsync();
        }
    }

    private static Session LoadFixture()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, FixturePath);
            if (File.Exists(candidate))
            {
                return SessionJson.Deserialize(File.ReadAllText(candidate));
            }

            directory = directory.Parent;
        }

        // Loudly, and never with a sample invented on the spot: a harness that quietly substitutes its own
        // data goes green while proving nothing, which is the failure this whole preview exists to avoid.
        throw new FileNotFoundException($"No {FixturePath} above {AppContext.BaseDirectory}.");
    }

    private async Task CaptureAsync()
    {
        Directory.CreateDirectory(_screenshotDirectory!);
        var fixture = LoadFixture();
        var draft = fixture.Draft!;

        foreach (var (name, session) in new (string, Session)[]
        {
            ("default", fixture),

            // The fixture has no inferred step, and the ⚠ treatment is the pane's most particular piece of
            // design - three signals, none of them colour alone (Spec §7). Derived from the fixture rather
            // than hand-written so the rest of the note stays the real one.
            ("low-confidence", fixture with
            {
                Draft = draft with
                {
                    Steps = [draft.Steps[0], draft.Steps[1] with { Confidence = StepConfidence.Low }, draft.Steps[2]],
                },
            }),
            ("partial", fixture with { PartialCapture = true }),
            ("purged", fixture with { FramesPurgedUnredacted = 3 }),
            ("local", fixture with { LocalOnly = true, Draft = draft with { Source = DraftSource.Local } }),

            // v0.4.1 Q4: the pane says what happened and offers Retry; the screenshots and transcript stay.
            ("draft-failed", fixture with { Draft = null }),
        })
        {
            Pane.DataContext = new NoteEditorViewModel(session, retryDraft: _ => Task.CompletedTask);
            foreach (var theme in new[] { AppTheme.Dark, AppTheme.Light, AppTheme.HighContrast })
            {
                ThemeManager.Apply(theme, Application.Current.Resources);
                await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.Render);
                UpdateLayout();
                WindowRenderer.Save(this, Path.Combine(_screenshotDirectory!, $"note-{name}-{theme.ToString().ToLowerInvariant()}.png"));
            }
        }

        Report();
        Application.Current.Shutdown();
    }

    private static void Report()
    {
        var errors = BindingErrors.Collected;
        foreach (var error in errors)
        {
            Console.Error.WriteLine(error);
        }

        // Printed as a count the workflow can read. A binding error means a label that should say
        // something says nothing, and every other check here would still pass.
        Console.WriteLine($"{errors.Count} binding errors");
    }
}

using ScreenTail.Core.Update;
using ScreenTail.Shared.Settings;

namespace ScreenTail.Core.Settings;

/// <summary>
/// One section of the Settings page as the page's Save bar sees it (Spec, Settings layout): what has
/// changed in it, why it cannot be saved as it is, its own save path, and putting it back.
/// </summary>
public interface ISettingsSection
{
    /// <summary>The section's name in the page's nav: "Capture", "Privacy".</summary>
    string Name { get; }

    /// <summary>Each setting changed and not saved, by the name of the card it is on. Empty when nothing is.</summary>
    IReadOnlyList<string> Changes { get; }

    /// <summary>Why the section cannot be saved as it is, or null.</summary>
    string? Problem { get; }

    /// <summary>Saves through the section's own path. Null when saved; otherwise why not, and its changes stay.</summary>
    Task<string?> SaveAsync(CancellationToken ct = default);

    /// <summary>Puts every field back to what was last saved, asking the service nothing.</summary>
    void Undo();
}

/// <summary>The Save bar's states (Spec §3): hidden with nothing changed, pending, saving, failed.</summary>
public enum SaveBarState
{
    Hidden,
    Pending,
    Saving,
    Failed,
}

/// <param name="Section">The section's nav name.</param>
/// <param name="Reason">Why it was not saved, in the section's own words.</param>
public sealed record SectionFailure(string Section, string Reason);

/// <param name="Section">The section's nav name.</param>
/// <param name="Change">The card the change is on.</param>
public sealed record PendingChange(string Section, string Change);

/// <summary>
/// The Settings page's one Save bar (Spec §1.1 "One Save per page, never a dialog", and the Settings
/// layout, an owner decision). Changes in every section collect here; Save writes each changed section
/// through that section's own save path, so what a setting does and where it is stored are unchanged. A
/// section that cannot be saved, or that the service refuses, is named with its reason and keeps its
/// changes pending, while the others commit. Undo puts every section back without asking the service.
/// A field the admin's policy locked never counts as a change (INV-11): each section compares what it
/// would save, and a locked field always saves as it was.
/// </summary>
public sealed class SettingsPage
{
    private IReadOnlyList<SectionFailure> _failures = [];
    private bool _saving;
    private bool _savedSome;

    public SettingsPage(IReadOnlyList<ISettingsSection> sections)
    {
        ArgumentNullException.ThrowIfNull(sections);
        Sections = sections;
    }

    public IReadOnlyList<ISettingsSection> Sections { get; }

    /// <summary>Every change not saved, section by section, in the page's order.</summary>
    public IReadOnlyList<PendingChange> AllChanges => [.. Sections.SelectMany(s => s.Changes.Select(c => new PendingChange(s.Name, c)))];

    public int PendingCount => Sections.Sum(s => s.Changes.Count);

    public bool HasPendingChanges => Sections.Any(s => s.Changes.Count > 0);

    /// <summary>The first section with a change, which the bar links to; null with nothing pending.</summary>
    public ISettingsSection? FirstChanged => Sections.FirstOrDefault(s => s.Changes.Count > 0);

    /// <summary>How many changes the named section holds, for the count beside it in the nav.</summary>
    public int PendingIn(string section) => Sections.Where(s => s.Name == section).Sum(s => s.Changes.Count);

    /// <summary>"2 changes not saved:", the words before the link to the first section; empty with nothing pending.</summary>
    public string Summary => PendingCount switch
    {
        0 => string.Empty,
        1 => "1 change not saved:",
        var n => $"{n} changes not saved:",
    };

    public SaveBarState State =>
        _saving ? SaveBarState.Saving
        : !HasPendingChanges ? SaveBarState.Hidden
        : Failures.Count > 0 ? SaveBarState.Failed
        : SaveBarState.Pending;

    /// <summary>The sections the last Save could not write and that still hold changes.</summary>
    public IReadOnlyList<SectionFailure> Failures =>
        [.. _failures.Where(f => Sections.Any(s => s.Name == f.Section && s.Changes.Count > 0))];

    /// <summary>
    /// The last Save's failures in the <c>&lt;What happened&gt;. &lt;What to do&gt;.</c> pattern (Spec §4):
    /// "Privacy was not saved. The capture service refused the settings. The other changes were saved."
    /// Null when there is nothing to say.
    /// </summary>
    public string? FailureMessage
    {
        get
        {
            var failures = Failures;
            if (failures.Count == 0)
            {
                return null;
            }

            var said = string.Join(" ", failures.Select(f => $"{f.Section} was not saved. {Sentence(f.Reason)}"));
            return _savedSome ? $"{said} The other changes were saved." : said;
        }
    }

    public bool CanSave => !_saving && HasPendingChanges;

    public bool CanUndo => !_saving && HasPendingChanges;

    /// <summary>
    /// Writes every changed section, one after another, each through its own save path. True when all
    /// were saved; false when any was not, which <see cref="Failures"/> names, or when a save is already
    /// on its way (nothing is sent twice).
    /// </summary>
    public async Task<bool> SaveAsync(CancellationToken ct = default)
    {
        if (!CanSave)
        {
            return !_saving && !HasPendingChanges;
        }

        _saving = true;
        var failures = new List<SectionFailure>();
        var saved = false;
        try
        {
            foreach (var section in Sections.Where(s => s.Changes.Count > 0).ToList())
            {
                if (section.Problem is { } problem)
                {
                    failures.Add(new SectionFailure(section.Name, problem));
                    continue;
                }

                var refusal = await section.SaveAsync(ct).ConfigureAwait(false);
                if (refusal is null)
                {
                    saved = true;
                }
                else
                {
                    failures.Add(new SectionFailure(section.Name, refusal));
                }
            }
        }
        finally
        {
            _failures = failures;
            _savedSome = saved;
            _saving = false;
        }

        return failures.Count == 0;
    }

    /// <summary>Puts every section back to what was last saved and forgets the last Save's failures.</summary>
    public void Undo()
    {
        if (_saving)
        {
            return;
        }

        foreach (var section in Sections)
        {
            section.Undo();
        }

        _failures = [];
        _savedSome = false;
    }

    private static string Sentence(string reason)
    {
        var trimmed = reason.Trim();
        return trimmed.Length == 0 || trimmed.EndsWith('.') || trimmed.EndsWith('?') ? trimmed : trimmed + ".";
    }
}

/// <summary>Settings → Capture on the page's Save bar (ST-080), saved by <see cref="CapturePanel.SaveAsync"/>.</summary>
/// <param name="startAtLogin">The shell's Run key, written after a save as Capture's own Save button did; null where there is none.</param>
public sealed class CaptureSection(CapturePanel panel, Func<bool, Task>? startAtLogin = null) : ISettingsSection
{
    public const string RemoteTools = "Remote tools";
    public const string Scope = "Capture scope";
    public const string StartingAndStopping = "Starting and stopping";
    public const string Hotkeys = "Hotkeys";
    public const string Screenshots = "Screenshots";
    public const string Microphone = "Microphone";
    public const string SpeechModel = "Speech model";
    public const string StartAtSignIn = "Start at sign-in";

    private readonly CapturePanel _panel = panel ?? throw new ArgumentNullException(nameof(panel));

    public string Name => "Capture";

    public IReadOnlyList<string> Changes
    {
        get
        {
            if (!_panel.Loaded)
            {
                return [];
            }

            var now = _panel.Current();
            var then = _panel.Baseline;
            var changes = new List<string>();
            Add(changes, RemoteTools, !now.DisabledTools.Order(StringComparer.Ordinal).SequenceEqual(then.DisabledTools.Order(StringComparer.Ordinal), StringComparer.Ordinal));
            Add(changes, Scope, now.CaptureAllWindows != then.CaptureAllWindows);
            Add(changes, StartingAndStopping, now.AutoStart != then.AutoStart || now.GraceSeconds != then.GraceSeconds);
            Add(changes, Hotkeys, now.Hotkeys.Count != then.Hotkeys.Count || now.Hotkeys.Any(h => !then.Hotkeys.TryGetValue(h.Key, out var other) || !string.Equals(h.Value, other, StringComparison.Ordinal)));
            Add(changes, Screenshots, !string.Equals(now.Sensitivity, then.Sensitivity, StringComparison.Ordinal));
            Add(changes, Microphone, !string.Equals(now.MicrophoneId, then.MicrophoneId, StringComparison.Ordinal));
            Add(changes, SpeechModel, !string.Equals(now.SpeechModel, then.SpeechModel, StringComparison.Ordinal));
            Add(changes, StartAtSignIn, now.StartUiAtLogin != then.StartUiAtLogin);
            return changes;
        }
    }

    public string? Problem => _panel.Unsavable;

    public async Task<string?> SaveAsync(CancellationToken ct = default)
    {
        if (!await _panel.SaveAsync(ct).ConfigureAwait(false))
        {
            return _panel.Problem ?? "The capture service did not save the settings. Try again.";
        }

        if (startAtLogin is not null)
        {
            await startAtLogin(_panel.StartUiAtLogin).ConfigureAwait(false);
        }

        return null;
    }

    public void Undo() => _panel.Undo();

    internal static void Add(List<string> changes, string name, bool changed)
    {
        if (changed)
        {
            changes.Add(name);
        }
    }
}

/// <summary>Settings → Privacy &amp; Redaction on the page's Save bar (ST-081), saved by <see cref="PrivacyPanel.SaveAsync"/>. Your data's actions act at once and are never on the bar.</summary>
public sealed class PrivacySection(PrivacyPanel panel) : ISettingsSection
{
    public const string LocalOnly = "Local-only mode";
    public const string ExcludedApps = "Excluded apps";
    public const string RedactionPatterns = "Redaction patterns";
    public const string Retention = "Retention";
    public const string Telemetry = "Usage telemetry";

    private readonly PrivacyPanel _panel = panel ?? throw new ArgumentNullException(nameof(panel));

    public string Name => "Privacy";

    public IReadOnlyList<string> Changes
    {
        get
        {
            if (!_panel.Loaded)
            {
                return [];
            }

            ClientSettings now = _panel.Current();
            var then = _panel.Baseline;
            var changes = new List<string>();
            CaptureSection.Add(changes, LocalOnly, now.LocalOnly != then.LocalOnly);
            CaptureSection.Add(changes, ExcludedApps, !now.ExcludedProcesses.SequenceEqual(then.ExcludedProcesses, StringComparer.OrdinalIgnoreCase));
            CaptureSection.Add(changes, RedactionPatterns, now.Ssn != then.Ssn || now.Cards != then.Cards || now.ApiKeys != then.ApiKeys || now.Passwords != then.Passwords || now.Emails != then.Emails
                || !now.CustomPatterns.SequenceEqual(then.CustomPatterns, StringComparer.Ordinal));
            CaptureSection.Add(changes, Retention, now.RetentionDays != then.RetentionDays);
            CaptureSection.Add(changes, Telemetry, now.Telemetry != then.Telemetry);
            return changes;
        }
    }

    public string? Problem => _panel.Unsavable;

    public async Task<string?> SaveAsync(CancellationToken ct = default) =>
        await _panel.SaveAsync(ct).ConfigureAwait(false) ? null : _panel.Problem ?? "The capture service did not save the settings. Try again.";

    public void Undo() => _panel.Undo();
}

/// <summary>
/// Settings → Integrations on the page's Save bar (ST-082): a card with a key typed or its site changed is
/// a change, stored by <see cref="IntegrationCard.SaveAsync"/>. Test connection, Remove and the company
/// mapping act at once, as they did, and are never on the bar.
/// </summary>
public sealed class IntegrationsSection(IntegrationsPanel panel) : ISettingsSection
{
    private readonly IntegrationsPanel _panel = panel ?? throw new ArgumentNullException(nameof(panel));

    public string Name => "Integrations";

    public IReadOnlyList<string> Changes => [.. _panel.Cards.Where(c => c.HasPendingChange).Select(c => c.Title)];

    /// <summary>Always null: each card is checked as it is saved, so one that is not filled in does not hold back the other.</summary>
    public string? Problem => null;

    /// <summary>Each changed card is stored on its own, so one that is refused does not hold back the other.</summary>
    public async Task<string?> SaveAsync(CancellationToken ct = default)
    {
        var refusals = new List<string>();
        foreach (var card in _panel.Cards.Where(c => c.HasPendingChange).ToList())
        {
            if (card.Unsavable is { } unsavable)
            {
                refusals.Add($"{card.Title}: {unsavable}");
                continue;
            }

            if (!await card.SaveAsync(ct).ConfigureAwait(false))
            {
                refusals.Add($"{card.Title}: {card.Problem ?? "the backend did not store it. Try again."}");
            }
        }

        return refusals.Count == 0 ? null : string.Join(" ", refusals);
    }

    public void Undo()
    {
        foreach (var card in _panel.Cards)
        {
            card.Undo();
        }
    }
}

/// <summary>
/// Settings → Updates on the page's Save bar (ST-113): the channel chosen is held here until Save, then
/// sent by <see cref="UpdatesPanel.ChooseAsync"/>. Install acts at once and is never on the bar. A channel
/// the admin chose cannot be chosen here (INV-11).
/// </summary>
public sealed class UpdatesSection(UpdatesPanel panel) : ISettingsSection
{
    public const string Channel = "Channel";

    private readonly UpdatesPanel _panel = panel ?? throw new ArgumentNullException(nameof(panel));

    public string Name => "Updates";

    /// <summary>The channel chosen and not yet saved, or null.</summary>
    public UpdateChannel? Pending { get; private set; }

    /// <summary>What the radio shows: the pending choice, or the channel in force.</summary>
    public UpdateChannel Shown => Pending ?? _panel.Channel;

    public IReadOnlyList<string> Changes => Pending is { } pending && pending != _panel.Channel ? [Channel] : [];

    public string? Problem => null;

    /// <summary>Holds <paramref name="channel"/> for the next Save. Ignored while the channel cannot be chosen, the admin's lock included.</summary>
    public void Choose(UpdateChannel channel)
    {
        if (_panel.CanChooseChannel)
        {
            Pending = channel == _panel.Channel ? null : channel;
        }
    }

    public async Task<string?> SaveAsync(CancellationToken ct = default)
    {
        if (Pending is not { } pending)
        {
            return null;
        }

        if (await _panel.ChooseAsync(pending, ct).ConfigureAwait(false) || _panel.Channel == pending)
        {
            Pending = null;
            return null;
        }

        return _panel.Problem ?? (_panel.ChannelLocked ? UpdatesPanel.LockedNote + "." : "The capture service did not change the channel. Try again.");
    }

    public void Undo() => Pending = null;
}

using System.Text.Json;
using ScreenTail.Core.Net;
using ScreenTail.Shared.Ipc;
using ScreenTail.Shared.Settings;

namespace ScreenTail.Core.Settings;

/// <summary>The capture settings on disk beside the store (ST-080). Not a secret; a bad file is the defaults.</summary>
public sealed class CaptureSettingsStore(string path)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public CaptureSettings Load()
    {
        try
        {
            return File.Exists(path) ? (JsonSerializer.Deserialize<CaptureSettings>(File.ReadAllText(path), Json) ?? new CaptureSettings()).WithDefaultsForNulls() : new CaptureSettings();
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            return new CaptureSettings();
        }
    }

    public bool Save(CaptureSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
            var temporary = path + ".tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(settings, Json));
            File.Move(temporary, path, overwrite: true);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}

/// <summary>How a capture settings document that crossed a boundary (the file, the pipe) is made whole.</summary>
public static class CaptureSettingsDocument
{
    /// <summary>
    /// The document with every null member read as absent, which is the shipped default: no list of
    /// disabled tools, no hotkey of the technician's own, medium sensitivity. JSON can say null where the
    /// type says a value always exists, and one that reached the hotkey or tool code threw at start, on
    /// every restart. Null and blank entries inside a list or a map are dropped for the same reason. The
    /// members that were given are kept, so one null does not cost the rest of the file.
    /// </summary>
    public static CaptureSettings WithDefaultsForNulls(this CaptureSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var defaults = new CaptureSettings();

        // Read through nullable views: the declared types promise these are never null, and a document
        // that came off a wire has not kept that promise.
        IReadOnlyList<string?>? tools = settings.DisabledTools;
        IReadOnlyDictionary<string, string>? hotkeys = settings.Hotkeys;
        string? sensitivity = settings.Sensitivity;

        var cleanTools = tools is null ? defaults.DisabledTools : [.. tools.Where(t => !string.IsNullOrWhiteSpace(t)).Select(t => t!)];
        var cleanHotkeys = hotkeys is null
            ? defaults.Hotkeys
            : hotkeys.Where(h => !string.IsNullOrWhiteSpace(h.Key) && !string.IsNullOrWhiteSpace(h.Value))
                .ToDictionary(h => h.Key, h => h.Value, StringComparer.Ordinal);

        return settings with
        {
            DisabledTools = cleanTools,
            Hotkeys = cleanHotkeys,
            Sensitivity = string.IsNullOrWhiteSpace(sensitivity) ? defaults.Sensitivity : sensitivity,
        };
    }
}

/// <summary>
/// The service's side of Settings → Capture (ST-080): the document with the registry's tools, the
/// device(s) found and what the policy forces beside it; save, validate and apply in place.
/// </summary>
public sealed class CaptureSettingsCommands(
    CaptureSettingsStore store,
    Func<IReadOnlyList<ToolRow>> tools,
    Func<IReadOnlyList<string>> microphones,
    Func<TenantPolicy> policy,
    Func<int> defaultGraceSeconds,
    Action<CaptureSettings> apply)
{
    private readonly CaptureSettingsStore _store = store ?? throw new ArgumentNullException(nameof(store));

    // Save and apply as one step, for the reason SettingsCommands gives: the file and the scope in force
    // must end on the same document when two connections save at once.
    private readonly Lock _saving = new();

    public Task<IpcEvent?> ReplyToAsync(IpcCommand command, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        return Task.FromResult<IpcEvent?>(command is GetCaptureSettingsCommand
            ? new CaptureSettingsReported
            {
                RequestId = command.RequestId,
                Settings = _store.Load(),
                Tools = tools(),
                Microphones = microphones(),
                PolicyForcesAllWindows = policy().CaptureAllWindows,
                DefaultGraceSeconds = defaultGraceSeconds(),
            }
            : null);
    }

    public Task<CommandResult?> HandleAsync(IpcCommand command, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        switch (command)
        {
            case SetCaptureSettingsCommand set:
                {
                    // A peer's document is read the way the file is: a null member is the default, not
                    // an exception in the middle of a save. A missing document is refused.
                    if (set.Settings is null)
                    {
                        return Task.FromResult<CommandResult?>(new CommandResult { RequestId = command.RequestId, Ok = false, Error = "No capture settings were sent." });
                    }

                    var settings = set.Settings.WithDefaultsForNulls();
                    var problems = settings.Problems();
                    if (problems.Count > 0)
                    {
                        return Task.FromResult<CommandResult?>(new CommandResult { RequestId = command.RequestId, Ok = false, Error = problems[0] });
                    }

                    lock (_saving)
                    {
                        if (!_store.Save(settings))
                        {
                            return Task.FromResult<CommandResult?>(new CommandResult { RequestId = command.RequestId, Ok = false, Error = "The settings could not be written to disk." });
                        }

                        apply(settings);
                    }

                    return Task.FromResult<CommandResult?>(new CommandResult { RequestId = command.RequestId, Ok = true });
                }

            case GetCaptureSettingsCommand:
                return Task.FromResult<CommandResult?>(new CommandResult { RequestId = command.RequestId, Ok = false, Error = "The capture settings could not be read." });

            default:
                return Task.FromResult<CommandResult?>(null);
        }
    }
}

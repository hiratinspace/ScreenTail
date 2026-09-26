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
            return File.Exists(path) ? JsonSerializer.Deserialize<CaptureSettings>(File.ReadAllText(path), Json) ?? new CaptureSettings() : new CaptureSettings();
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
                    var problems = set.Settings.Problems();
                    if (problems.Count > 0)
                    {
                        return Task.FromResult<CommandResult?>(new CommandResult { RequestId = command.RequestId, Ok = false, Error = problems[0] });
                    }

                    if (!_store.Save(set.Settings))
                    {
                        return Task.FromResult<CommandResult?>(new CommandResult { RequestId = command.RequestId, Ok = false, Error = "The settings could not be written to disk." });
                    }

                    apply(set.Settings);
                    return Task.FromResult<CommandResult?>(new CommandResult { RequestId = command.RequestId, Ok = true });
                }

            case GetCaptureSettingsCommand:
                return Task.FromResult<CommandResult?>(new CommandResult { RequestId = command.RequestId, Ok = false, Error = "The capture settings could not be read." });

            default:
                return Task.FromResult<CommandResult?>(null);
        }
    }
}

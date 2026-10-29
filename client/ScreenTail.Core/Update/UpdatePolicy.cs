using System.Text.Json;
using System.Text.Json.Serialization;
using ScreenTail.Core.Net;

namespace ScreenTail.Core.Update;

/// <summary>
/// What the tenant's policy and the technician's choice mean for updates, as they stand now (ST-113, INV-11).
/// </summary>
/// <param name="Channel">The channel to follow. Null when the admin set one this version does not know: no checks then.</param>
/// <param name="ChannelLocked">The admin chose the channel, so Settings shows it read-only.</param>
/// <param name="Every">How often to check. Null when the admin turned checks off (an MSP that pushes updates with its own tool).</param>
/// <param name="UnderLocalOnly">The admin allows update checks while local-only mode is on. Off unless they say so.</param>
/// <param name="Problem">Why there is no channel, when there is none.</param>
public sealed record UpdatePolicy(UpdateChannel? Channel, bool ChannelLocked, TimeSpan? Every, bool UnderLocalOnly, string? Problem = null);

public static class UpdatePolicies
{
    public const int DefaultCheckHours = 24;

    public const int MaxCheckHours = 168;

    public const string UnknownChannel = "Your administrator chose an update channel this version of ScreenTail does not know, so it does not check for updates.";

    /// <summary>
    /// The admin's policy where it says anything, the technician's channel otherwise. The interval and
    /// the local-only exception are the admin's alone: a technician cannot make their own machine check
    /// more often, or check at all under local-only.
    /// </summary>
    public static UpdatePolicy Resolve(TenantPolicy policy, UpdateChannel technicianChoice)
    {
        throw new NotImplementedException();
    }
}

/// <summary>
/// The technician's own channel, in a file of its own beside the settings (ST-113 AC3).
///
/// Not a field of the Privacy settings document: that one is saved whole from whatever the panel last
/// read, so a Privacy save would put back the channel it read before this one changed.
/// </summary>
public sealed class UpdatePreferences(string path)
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    /// <summary>Stable when there is no file or it will not read.</summary>
    public UpdateChannel Load()
    {
        try
        {
            if (!File.Exists(path))
            {
                return UpdateChannel.Stable;
            }

            var stored = JsonSerializer.Deserialize<Stored>(File.ReadAllText(path), Json);
            return UpdateChannels.TryParse(stored?.Channel, out var channel) ? channel : UpdateChannel.Stable;
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            return UpdateChannel.Stable;
        }
    }

    /// <summary>False when the file could not be written; the choice then lasts until the service stops.</summary>
    public bool Save(UpdateChannel channel)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
            var temporary = path + ".tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(new Stored { Channel = channel.ToWire() }, Json));
            File.Move(temporary, path, overwrite: true);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private sealed class Stored
    {
        [JsonPropertyName("channel")]
        public string? Channel { get; init; }
    }
}

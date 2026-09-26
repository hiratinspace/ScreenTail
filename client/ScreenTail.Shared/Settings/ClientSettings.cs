using System.Text.Json.Serialization;

namespace ScreenTail.Shared.Settings;

/// <summary>
/// The technician's own settings (ST-081, Spec §5 S6), as the service keeps them on disk. The admin's
/// policy (ST-047) sits on top: a field the policy locked is applied from the policy whatever this says,
/// and Settings shows it read-only with "Set by your admin".
/// </summary>
public sealed record ClientSettings
{
    public const int MinRetentionDays = 1;

    public const int MaxRetentionDays = 30;

    [JsonPropertyName("local_only")]
    public bool LocalOnly { get; init; }

    /// <summary>Usage telemetry: the fields in <c>shared/contracts/session-metric.v1.json</c> and nothing else. Off until switched on.</summary>
    [JsonPropertyName("telemetry")]
    public bool Telemetry { get; init; }

    [JsonPropertyName("retention_days")]
    public int RetentionDays { get; init; } = 7;

    [JsonPropertyName("ssn")]
    public bool Ssn { get; init; } = true;

    [JsonPropertyName("cards")]
    public bool Cards { get; init; } = true;

    [JsonPropertyName("api_keys")]
    public bool ApiKeys { get; init; } = true;

    [JsonPropertyName("passwords")]
    public bool Passwords { get; init; } = true;

    [JsonPropertyName("emails")]
    public bool Emails { get; init; }

    [JsonPropertyName("custom_patterns")]
    public IReadOnlyList<string> CustomPatterns { get; init; } = [];

    /// <summary>Process names, without path or extension, that are never captured (Spec §5 S6 "Excluded apps").</summary>
    [JsonPropertyName("excluded_processes")]
    public IReadOnlyList<string> ExcludedProcesses { get; init; } = [];

    /// <summary>A process name is the part Windows shows in Task Manager: no path, no extension, no spaces around it.</summary>
    public static string? ProcessNameProblem(string? process)
    {
        if (string.IsNullOrWhiteSpace(process))
        {
            return "Enter the process name, as Task Manager shows it.";
        }

        if (process.IndexOfAny(['\\', '/', ':', '"', '<', '>', '|', '*', '?']) >= 0 || process.Contains(".exe", StringComparison.OrdinalIgnoreCase))
        {
            return "A process is its name, like keepass, not a path or a file name.";
        }

        return null;
    }
}

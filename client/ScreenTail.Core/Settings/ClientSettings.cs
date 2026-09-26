using System.Text.Json;
using ScreenTail.Core.Privacy;
using ScreenTail.Shared.Settings;

namespace ScreenTail.Core.Settings;

/// <summary>The rules over <see cref="ClientSettings"/> that need the redaction engine, kept here so the shared contracts assembly stays contract-only.</summary>
public static class ClientSettingsRules
{
    /// <summary>Everything wrong with this document, in the words the screen shows. Empty means it can be saved.</summary>
    public static IReadOnlyList<string> Problems(this ClientSettings settings)
    {
        var problems = new List<string>();
        if (settings.RetentionDays is < ClientSettings.MinRetentionDays or > ClientSettings.MaxRetentionDays)
        {
            problems.Add($"Retention is between {ClientSettings.MinRetentionDays} and {ClientSettings.MaxRetentionDays} days.");
        }

        foreach (var pattern in settings.CustomPatterns)
        {
            if (RedactionPolicy.ValidateCustomPattern(pattern) is { } problem)
            {
                problems.Add($"The pattern \"{pattern}\" cannot be saved: {problem}");
            }
        }

        foreach (var process in settings.ExcludedProcesses)
        {
            if (ClientSettings.ProcessNameProblem(process) is { } problem)
            {
                problems.Add(problem);
            }
        }

        return problems;
    }

    public static RedactionPolicy ToRedactionPolicy(this ClientSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        return new RedactionPolicy
        {
            Ssn = settings.Ssn,
            Cards = settings.Cards,
            ApiKeys = settings.ApiKeys,
            Passwords = settings.Passwords,
            Emails = settings.Emails,
            CustomPatterns = settings.CustomPatterns,
        };
    }
}


/// <summary>The settings on disk beside the store. Not a secret; a bad file is the defaults, overwritten on the next save.</summary>
public sealed class ClientSettingsStore(string path)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public string Path => path;

    public ClientSettings Load()
    {
        try
        {
            return File.Exists(path) ? JsonSerializer.Deserialize<ClientSettings>(File.ReadAllText(path), Json) ?? new ClientSettings() : new ClientSettings();
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            return new ClientSettings();
        }
    }

    public bool Save(ClientSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        try
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(path))!);
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

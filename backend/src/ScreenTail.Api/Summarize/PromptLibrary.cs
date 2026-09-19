using System.Reflection;

namespace ScreenTail.Api.Summarize;

/// <summary>
/// The note prompt, shipped with the service (ST-061, ST-063).
///
/// Embedded rather than read from disk so that a deployment cannot drift from the prompt its
/// post-conditions were written against: <see cref="DraftValidator"/> refuses a draft from any other
/// version, and the two have to travel together to make that check mean anything.
/// </summary>
public static class PromptLibrary
{
    /// <summary>The system prompt handed to the model with every session.</summary>
    public static string Note()
    {
        var assembly = Assembly.GetExecutingAssembly();
        var name = assembly.GetManifestResourceNames().Single(n => n.EndsWith("note_v1.md", StringComparison.Ordinal));
        using var stream = assembly.GetManifestResourceStream(name)!;
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}

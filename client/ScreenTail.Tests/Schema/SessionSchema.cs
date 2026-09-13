using Json.Schema;

namespace ScreenTail.Tests.Schema;

/// <summary>
/// The session schema, loaded once.
///
/// <see cref="JsonSchema.FromFile"/> registers the document globally by its <c>$id</c>, so a second class
/// loading the same file throws "Overwriting registered schemas is not permitted" — and it throws in
/// whichever test happened to run second, which makes it look like that test's fault.
/// </summary>
internal static class SessionSchema
{
    private static readonly Lazy<JsonSchema> Loaded = new(() =>
        JsonSchema.FromFile(Path.Combine(Root, "session.v1.json")));

    public static string Root => Path.Combine(AppContext.BaseDirectory, "Schema");

    public static JsonSchema Instance => Loaded.Value;

    public static EvaluationOptions Options => new() { RequireFormatValidation = true, OutputFormat = OutputFormat.List };
}

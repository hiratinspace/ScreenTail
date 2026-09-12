using System.Text.Json;
using System.Text.Json.Serialization;

namespace ScreenTail.Shared.Schema;

/// <summary>
/// Reads and writes session.v1 documents with the generated types (Generated/SessionV1.g.cs). The settings
/// mirror the schema: unknown properties are rejected (additionalProperties: false, which also enforces
/// INV-2 for keyboard events), required members must be present, and non-nullable members can't be null.
/// Both directions also run <see cref="SessionValidator"/>, so a document that breaks the cross-field rules
/// is never read in or written out.
/// </summary>
public static class SessionJson
{
    public static JsonSerializerOptions Options { get; } = CreateOptions();

    /// <exception cref="JsonException">The text isn't a session.v1 document, or it breaks its rules.</exception>
    public static Session Deserialize(string json)
    {
        var session = JsonSerializer.Deserialize<Session>(json, Options) ?? throw new JsonException("The session document is null.");
        var problems = SessionValidator.Validate(session);
        if (problems.Count > 0)
        {
            throw new JsonException("The session document breaks session.v1 rules: " + string.Join("; ", problems));
        }

        return session;
    }

    /// <exception cref="InvalidOperationException">The session breaks session.v1 rules and must not be written.</exception>
    public static string Serialize(Session session)
    {
        var problems = SessionValidator.Validate(session);
        if (problems.Count > 0)
        {
            throw new InvalidOperationException("Refusing to write a session that breaks session.v1 rules: " + string.Join("; ", problems));
        }

        return JsonSerializer.Serialize(session, Options);
    }

    private static JsonSerializerOptions CreateOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.General)
        {
            AllowOutOfOrderMetadataProperties = true,
            RespectNullableAnnotations = true,
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        };
        options.MakeReadOnly(populateMissingResolver: true);
        return options;
    }
}

using System.Text.Json;
using System.Text.Json.Serialization;

namespace ScreenTail.Shared.Schema;

/// <summary>
/// Reads and writes session.v1 documents with the generated types (Generated/SessionV1.g.cs). The settings
/// mirror the schema: unknown properties are rejected (additionalProperties: false, which also enforces
/// INV-2 for keyboard events), required members must be present, and non-nullable members can't be null.
/// </summary>
public static class SessionJson
{
    public static JsonSerializerOptions Options { get; } = CreateOptions();

    public static Session Deserialize(string json) =>
        JsonSerializer.Deserialize<Session>(json, Options) ?? throw new JsonException("The session document is null.");

    public static string Serialize(Session session) => JsonSerializer.Serialize(session, Options);

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

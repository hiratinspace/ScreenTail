using System.Text.Json;
using System.Text.Json.Nodes;
using Json.Schema;
using ScreenTail.Shared.Schema;

namespace ScreenTail.Tests.Schema;

/// <summary>
/// ST-003: every example in shared/schema/examples behaves as its folder says, and valid examples round-trip
/// through the generated C# types. The same examples also run through ajv (shared/schema, npm test).
/// </summary>
public class SessionSchemaTests
{
    private static readonly string SchemaRoot = Path.Combine(AppContext.BaseDirectory, "Schema");
    private static readonly JsonSchema Schema = JsonSchema.FromFile(Path.Combine(SchemaRoot, "session.v1.json"));
    private static readonly EvaluationOptions Evaluation = new() { RequireFormatValidation = true, OutputFormat = OutputFormat.List };

    public static TheoryData<string> ValidExamples => Examples("valid");

    public static TheoryData<string> InvalidExamples => Examples("invalid");

    [Theory]
    [MemberData(nameof(ValidExamples))]
    public void ValidExamplePassesTheSchema(string example)
    {
        var results = Evaluate(example);

        Assert.True(results.IsValid, JsonSerializer.Serialize(results));
    }

    [Theory]
    [MemberData(nameof(InvalidExamples))]
    public void InvalidExampleFailsTheSchema(string example)
    {
        Assert.False(Evaluate(example).IsValid);
    }

    [Theory]
    [MemberData(nameof(ValidExamples))]
    public void ValidExampleRoundTripsThroughGeneratedTypes(string example)
    {
        var json = Read(example);

        var roundTripped = SessionJson.Serialize(SessionJson.Deserialize(json));

        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(json), JsonNode.Parse(roundTripped)), roundTripped);
    }

    [Theory]
    [InlineData("inv2-typing-burst-with-key.json")] // INV-2: no field can hold a key
    [InlineData("inv1-frame-missing-redaction-pending.json")] // INV-1: redaction_pending is required
    [InlineData("inv1-pending-frame-with-ocr-text.json")] // INV-1: SessionValidator
    [InlineData("inv1-redacted-frame-without-redacted-at.json")] // INV-1: SessionValidator
    [InlineData("inv9-unknown-speaker.json")] // INV-9: tech (or reserved end_user) only
    [InlineData("unknown-event-type.json")]
    [InlineData("missing-schema-version.json")] // required member
    public void GeneratedTypesRejectWhatTheSchemaRejects(string example)
    {
        var json = Read(Path.Combine("invalid", example));

        Assert.ThrowsAny<JsonException>(() => SessionJson.Deserialize(json));
    }

    [Fact]
    public void EventsDeserializeToTheirConcreteTypes()
    {
        var session = SessionJson.Deserialize(Read(Path.Combine("valid", "printer-offline-drafted.json")));

        Assert.IsType<FocusEvent>(session.Events[0]);
        var burst = Assert.Single(session.Events.OfType<TypingBurstEvent>());
        Assert.Equal(9, burst.CharCount);
        Assert.Equal(205_000, burst.TsMs);
        Assert.NotNull(session.Draft);
        Assert.Equal(StepConfidence.Low, session.Draft.Steps[1].Confidence);
    }

    private static TheoryData<string> Examples(string kind)
    {
        var data = new TheoryData<string>();
        foreach (var path in Directory.EnumerateFiles(Path.Combine(SchemaRoot, "examples", kind), "*.json").Order(StringComparer.Ordinal))
        {
            data.Add(Path.Combine(kind, Path.GetFileName(path)));
        }

        return data;
    }

    private static string Read(string example) => File.ReadAllText(Path.Combine(SchemaRoot, "examples", example));

    private static EvaluationResults Evaluate(string example)
    {
        using var document = JsonDocument.Parse(Read(example));
        return Schema.Evaluate(document.RootElement, Evaluation);
    }
}

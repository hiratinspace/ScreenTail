using System.Globalization;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace ScreenTail.Api.Providers.Llm;

/// <summary>
/// The note's JSON schema in the form Claude's structured output accepts (ST-063).
///
/// <c>research/prompts/schema.json</c> is the contract, shipped inside the service. Structured output
/// supports a subset of JSON Schema and answers anything outside it with a 400: no string lengths, no
/// numeric bounds, no array sizes beyond <c>minItems</c> of 0 or 1, and every object closed. So those
/// constraints are removed here and written into the field's description instead, which is what
/// Anthropic's own SDK helpers do. The model still reads every one of them, and everything else in the
/// schema (the fields, their types, the enums, the constant prompt version, no extra properties) is
/// enforced by the API's constrained decoding. That is more than the Gemini request gets, which asks
/// only for JSON; <see cref="Summarize.DraftValidator"/> checks both the same way afterwards.
/// </summary>
public static class ClaudeSchema
{
    /// <summary>Keywords the API refuses, and how each is said in a description.</summary>
    private static readonly Dictionary<string, string> Moved = new(StringComparer.Ordinal)
    {
        ["minLength"] = "at least {0} characters",
        ["maxLength"] = "at most {0} characters",
        ["maxItems"] = "at most {0} items",
        ["minimum"] = "at least {0}",
        ["maximum"] = "at most {0}",
    };

    /// <summary>Keywords that describe the document rather than the value. Dropped.</summary>
    private static readonly string[] Dropped = ["$schema", "$id", "title"];

    private static readonly Lazy<IReadOnlyDictionary<string, JsonElement>> Cached = new(Build);

    /// <summary>The schema as structured output takes it, keyed by top-level keyword.</summary>
    public static IReadOnlyDictionary<string, JsonElement> Wire() => Cached.Value;

    private static Dictionary<string, JsonElement> Build()
    {
        var assembly = Assembly.GetExecutingAssembly();
        var name = assembly.GetManifestResourceNames().Single(n => n.EndsWith("schema.json", StringComparison.Ordinal));
        using var stream = assembly.GetManifestResourceStream(name)!;
        var root = JsonNode.Parse(stream)!.AsObject();

        Simplify(root);

        return root.ToDictionary(
            property => property.Key,
            property => JsonSerializer.SerializeToElement(property.Value),
            StringComparer.Ordinal);
    }

    /// <summary>Rewrites one schema node, and everything under it, in place.</summary>
    private static void Simplify(JsonNode? node)
    {
        if (node is JsonArray array)
        {
            foreach (var item in array)
            {
                Simplify(item);
            }

            return;
        }

        if (node is not JsonObject schema)
        {
            return;
        }

        foreach (var keyword in Dropped)
        {
            _ = schema.Remove(keyword);
        }

        var notes = new List<string>();
        foreach (var (keyword, phrase) in Moved)
        {
            if (schema[keyword] is JsonValue value)
            {
                notes.Add(string.Format(CultureInfo.InvariantCulture, phrase, value.ToJsonString()));
                _ = schema.Remove(keyword);
            }
        }

        // Only 0 and 1 are accepted; anything larger is said instead.
        if (schema["minItems"] is JsonValue min && min.TryGetValue<int>(out var least) && least > 1)
        {
            notes.Add(string.Format(CultureInfo.InvariantCulture, "at least {0} items", least));
            _ = schema.Remove("minItems");
        }

        if (notes.Count > 0)
        {
            var said = schema["description"]?.GetValue<string>();
            var constraint = "(" + string.Join("; ", notes) + ")";
            schema["description"] = string.IsNullOrEmpty(said) ? constraint : said + " " + constraint;
        }

        if (schema["type"] is JsonValue type && type.TryGetValue<string>(out var kind) && kind == "object")
        {
            schema["additionalProperties"] = false;
        }

        // Maps of name to schema: the names are a note's field names, never keywords, so only the
        // schemas under them are rewritten.
        foreach (var map in new[] { "properties", "$defs", "definitions" })
        {
            if (schema[map] is JsonObject named)
            {
                foreach (var (_, child) in named)
                {
                    Simplify(child);
                }
            }
        }

        // Single schemas and lists of them.
        foreach (var keyword in new[] { "items", "anyOf", "allOf" })
        {
            Simplify(schema[keyword]);
        }
    }
}

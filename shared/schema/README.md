# Session schema (`session.v1`)

[`session.v1.json`](session.v1.json) (JSON Schema 2020-12) is the contract for one captured session: timeline events, screenshots, transcript and the drafted note. The client store export, the bundle builder (ST-060), fixtures (ST-006, ST-030), the prompt (ST-061) and Review (ST-074) all use it.

## What the schema enforces

| Rule | How |
|---|---|
| Every event, frame and transcript segment is ordered by `ts_ms`, monotonic milliseconds since the session started | `TsMs` on every timeline item |
| **INV-2**, no raw keystrokes | Keyboard events (`typing_burst`, `shortcut`, `enter`) have no field that can hold a key, and `additionalProperties: false` rejects one being added |
| **INV-1**, frames go through redaction first | Frames require `redaction_pending`. A pending frame can't carry `ocr_text` or `redacted_at`; a redacted frame must have `redacted_at` |
| **INV-9**, the technician's mic only | `speaker` is `tech`; `end_user` is reserved for v1.2 (ST-123) |
| Window titles aren't stored in events | Focus events carry the process and scope only |
| Optional values are omitted rather than written as `null` | Generated serializers skip nulls, so documents round-trip exactly |

## Generated code

`npm run codegen` writes:

- `client/ScreenTail.Shared/Generated/SessionV1.g.cs`: immutable C# records with `required` members. `SessionEvent` is a `System.Text.Json` polymorphic base, and `ScreenTail.Shared.Schema.SessionJson` holds the matching serializer settings.
- `SessionValidator` (hand-written, next to `SessionJson`) checks what the types can't: the frame rule above, `schema_version`, `ts_ms` ordering, and that every `frame_id`, `segment_id`, `frame_refs` and `transcript_refs` points at something that exists. `SessionJson` runs it on both read and write, so an invalid session is neither read in nor written out.
- `web/src/generated/session.v1.ts`: TypeScript types (json-schema-to-typescript).

Generated files are committed. CI regenerates them and fails if they differ (`npm run check`), so the schema and the code can't drift. The C# generator (`codegen/generate.mjs`) supports only what the schema uses today and throws on anything else. Extend it in the same PR as a schema change that needs it.

## Examples

`examples/valid/*.json` must pass and `examples/invalid/*.json` must fail. They're checked twice: by ajv (`npm test`) and by `ScreenTail.Tests/Schema`, which also round-trips every valid example through the generated C# types. Invalid examples are named after the rule they break.

## Changing the schema

1. Edit `session.v1.json`. Breaking changes need `session.v2`.
2. Add or adjust examples.
3. From `shared/schema`, run `npm ci`, then `npm test`, then `npm run codegen`.
4. Run `dotnet test client/ScreenTail.sln` and `cd web && npm run typecheck`.
5. Commit the schema, the examples and the regenerated files together.

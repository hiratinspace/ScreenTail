# Prompts

Versioned prompt files and their output contract. `note_v1.md` (ST-061) is the session-to-note prompt:
strict output schema, post-conditions that reject invented steps, fake quotations, dangling frame
references and leaked secrets. Any prompt change runs the eval harness in CI (Guide §5); the harness's
draft-quality half arrives with ST-062, on ST-030's golden sessions. `note_local_v1.md` arrives with ST-065.

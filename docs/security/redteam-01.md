# Red team, round one: the redaction pipeline at the text level

**2026-09-25. ST-115, first pass.** Forty-one adversarial inputs through `RedactionEngine` with the
default policy, at the level OCR reads off a screen and the transcriber hears — the same text path
every frame and every transcript segment take. Round two, real screenshots through the VM harness
(ST-013), waits for that harness. Every row is a test in
`client/ScreenTail.Tests/Privacy/RedTeamTests.cs`; a finding that was fixed has its regression there.

## What was tried, and what happened

| # | Scenario | Before | Now |
|---|---|---|---|
| R01 | SSN with dots, `123.45.6789` | **survived** | masked — dots accepted as a separator |
| R02 | SSN with spaces after a cue | masked | masked |
| R03 | Bare nine digits after `SSN#` | masked | masked |
| R04 | `Social Security Number = …` | masked | masked |
| R05 | Card with dots, `4111.1111.1111.1111` | **survived** | masked — dots accepted between groups |
| R06 | Card beside a CVV | masked | masked |
| R07 | Amex in 4-6-5 groups | masked | masked |
| R08 | Card with dashes after a cue | masked | masked |
| R09 | `aws_secret_access_key = …` | masked | masked |
| R10 | GitHub fine-grained token `github_pat_…` | **survived** | masked — its own shape |
| R11 | Stripe `sk_live_…` after "Secret key" | masked | masked; `sk_live_`/`sk_test_` now a shape of their own too |
| R12 | Azure `AccountKey=…` in a connection string | **survived** | masked — `account` joins the key cues |
| R13 | `Password=…;` in a connection string | masked | masked |
| R14 | `Bearer ya29.…` (Google OAuth) | **survived** | masked — a dot is allowed inside a token after a cue, and `ya29.` is a shape |
| R15 | Slack incoming-webhook URL | **survived** | masked — the URL shape |
| R16 | `?token=…` in a URL | masked | masked |
| R17 | Google API key `AIza…` | **survived** | masked — the shape, and `maps` as a key cue |
| R18 | SendGrid `SG.….…` | **survived** | masked — the shape |
| R19 | PGP private key block | **survived** | masked — `PRIVATE KEY BLOCK` accepted in the armour |
| R20 | EC private key | masked | masked |
| R21 | `OPENAI_API_KEY=sk-proj-…` | masked | masked |
| R22 | npm token `npm_…` | masked (cue) | masked; its own shape too |
| R23 | "the wifi password is Winter 2026" | masked | masked |
| R24 | `pass: hunter2` | **survived** | masked — `pass` followed by `:` or `=` is a cue |
| R25 | "temporary password Welcome1! then…" | masked | masked |
| R26 | `PIN: 4821` | masked | masked |
| R27 | "their passcode is 0791" | masked | masked |
| R28 | `password=P%40ssw0rd&username=jo` | masked | masked |
| N01–N08 | Ticket numbers, phone numbers, order numbers, versions, IPs, MACs, serials, invoices, dates | left alone | left alone |
| N09 | "the password field was empty…" | left alone | left alone |
| N10 | "reset the password and the PIN for the user…" | **"user" masked** | left alone |
| N11 | "…choose a new password at first login" | **"first" masked** | left alone |
| N12 | "the API key in the vault is fine…" | left alone | left alone |
| F01 | A card split across OCR words | masked as one region | masked as one region |

## The two false positives, and the rule that fixed them

The spoken-password rule walks past joining words ("is", "the", "on") to find the secret, and took
the first ordinary word it reached as the password. "The PIN for the user" and "a new password at
first login" reached "user" and "first". The rule now distinguishes joining words that *assert* a
value — "is", "was", "set to", a colon or an equals sign — from articles and prepositions that
merely continue the sentence. An ordinary-looking word is masked only when something asserted it;
a word that looks like a secret (a digit, a symbol, a capital inside it, twelve or more characters)
is masked either way. Every case in `SpokenPasswordTests` and `PatternEngineTests` still holds.

## Accepted gaps, with the reason

- **Nine bare digits with no cue** (`123456789`) are not an SSN. Nine-digit runs are order numbers
  and phone numbers far more often, and ST-042's false-positive budget is 2%.
- **Email addresses** are masked only under the strict policy. A technician's note names people by
  address on purpose; the tenant's policy decides.
- **One-time codes** ("verification code 483920") are not masked. They expire in minutes and a rule
  broad enough to catch them masks every six-digit number on a screen.
- **IBANs and bank account numbers** are not in the library. They are PII rather than credentials;
  a pattern is a small addition once a tenant needs one (custom patterns already exist).
- **OCR substitutions inside a number** (`411O` for `4110`) defeat Luhn. Round two measures how
  often the OCR engine does this on real screens before a rule guesses at it.
- **Secrets with no shape and no cue** — a random string on its own line — cannot be told from an
  identifier. The exclusion list and the sensitive-context guard (password fields, sign-in pages)
  are the defence there, not the pattern library.

## Round two

Real screenshots on the VM harness (ST-013): the same scenarios as pixels, plus what OCR does to
them; the transcript path with the speech model's actual output for R23–R27; and a measured
false-positive rate on ST-030's labelled frames, which is ST-042's remaining gate.

# Providers

How ScreenTail talks to a PSA and a documentation platform (ST-090).

Two interfaces, one shape, and the client sees neither. The client asks the backend to publish; the
backend decides which provider that means. A client that knew about ConnectWise would need a ConnectWise
release to support HaloPSA, and would need a second copy of a customer's PSA credentials on a
technician's laptop. `TheClientNeverNamesAConcreteProvider` in the client test suite fails the build if
that changes.

## The interfaces

| Interface | For | v1 | Later |
|---|---|---|---|
| `IPsaProvider` | Tickets, notes, time entries | ConnectWise Manage (ST-091) | HaloPSA (ST-121) |
| `IDocProvider` | Companies, knowledge-base articles | Hudu (ST-095) | IT Glue |

They are separate because the two are chosen independently — an MSP may run ConnectWise with Hudu, or
Autotask with IT Glue — and because the failures differ. A PSA refusing a note stops the billing record;
a documentation platform refusing an article does not.

## Failures are values, not exceptions

Every call returns `ProviderResult<T>`: the thing, or a `ProviderError`. Exceptions are for bugs. A PSA
being down, a key being wrong and a ticket being closed are ordinary outcomes of pressing Publish, each
with a different thing for the technician to do, so they arrive as values the compiler makes the caller
look at.

```csharp
var result = await psa.AddNoteAsync(note, ct);
if (!result.Ok)
{
    // result.Error.ToString() is already Spec §4's sentence, ready to show.
}
```

### The error kinds

Six, and deliberately no more. A taxonomy with thirty entries becomes thirty branches nobody writes, and
each unhandled one falls through to "something went wrong" — the message Spec §4 exists to forbid. These
are the cases where the next step genuinely differs.

| Kind | Means | The technician should |
|---|---|---|
| `Unauthenticated` | Credentials wrong, missing or expired | Fix them in Settings |
| `Forbidden` | Credentials fine, this account may not | Ask an administrator |
| `NotFound` | Ticket, company or article is not there | Pick a different one |
| `Invalid` | The provider refused what we sent | Nothing — it is our bug |
| `Unavailable` | Rate limit, timeout, outage | Wait; this is the only retryable one |
| `Unknown` | A write left and nothing said whether it was saved | Look in the PSA or platform before publishing again |

**Only `Unavailable` is retryable**, and `ProviderError.Retryable` is the single place that is decided.
Retrying a wrong API key is how an integration becomes an account lockout; retrying an invalid payload is
the same rejection several hundred times.

**A write that may have landed is `Unknown`, not `Unavailable`** (2026-10-12 review). A note, time
entry, document or article that timed out, lost its connection or met a server error after its body left
may already be saved, and sending it again is a second one on the ticket. `ProviderHttp` watches the
request body: a write is tried again only when none of it left, or when the provider answered 429.

### Messages

`ProviderError` carries two sentences, because Spec §4 requires both:

> `<What happened>. <What to do>.`
>
> "ConnectWise rejected the request: clientId header missing. Add your clientId in Settings → Integrations."

The first half is a support call on its own. The contract tests assert both are present and that both end
in a full stop, so the pattern is enforced rather than reviewed.

Messages are shown to a technician verbatim and may be pasted into a ticket, so they carry the provider's
own words about what it rejected and never a stack trace, a URL with a path, or a token.

## The contract harness

`PsaProviderContract` and `DocProviderContract` in the test project hold the rules every implementation
must follow. Derive, supply a provider and a way to break it, and the contract runs:

```csharp
public sealed class ConnectWiseMeetsTheContract : PsaProviderContract
{
    protected override IPsaProvider Provider => _connectWise;
    protected override string KnownTicketId => "48213";
    protected override void Break(ProviderError? error) => _http.NextFailure = error;
}
```

A connector that cannot pass these is not finished, whatever its own tests say. Writing them once means
ConnectWise and HaloPSA cannot disagree about what a missing ticket does or whether a wrong key is worth
retrying.

The rules cover: failures are values; only outages retry; every message has both halves; a search under
three characters is refused (Spec §5 S3) while one that matches nothing is an empty list rather than an
error; a result names the company as well as the ticket, so nobody publishes to the right number at the
wrong client; a note on a missing ticket is `NotFound` rather than `Invalid`; and a published note comes
back with an identifier, without which a retry after a dropped connection cannot tell "already published"
from "not published".

## ConnectWise Manage (ST-091)

`ConnectWise/ConnectWiseProvider` is the first real one. It is never in the container as an
`IPsaProvider`: `IPsaProviderFactory` builds one per tenant from the credential the vault holds
(ST-009), with the tenant's own site as the base address and the deployment's `ConnectWise:ClientId`
on every request. `docs/integrations/connectwise.md` has the routes, what a tenant supplies, and the
sandbox check that has not been run yet. Its tests run against `ScriptedConnectWise`, recorded shapes
of the API's answers, and it passes the contract harness like the fake does.

## Hudu (ST-095)

`Hudu/HuduProvider` is the documentation platform, built per tenant by `IDocProviderFactory` from the
vault's `hudu` credential like ConnectWise. Companies are paged and cached ten minutes per tenant in
`HuduCompanyCache`; an article is created as a draft under its company and its attachments are uploaded
against it; a company the tenant does not have is refused before anything is sent.
`docs/integrations/hudu.md` says what to verify against a real Hudu first — that `draft` is honoured.

Both providers send through `ProviderHttp`, so they cannot disagree about what a 429 means: only an
outage is retried, three attempts at most, and a write only when nothing of it left or the answer was
a 429. The wait is the exponential backoff with jitter, or the provider's Retry-After when that is
longer; a Retry-After beyond `MaxRetryWait` (five seconds) is not waited out, and the error carries it
instead, with its advice in the same terms (`ProviderHttp.TryAgain`: "Try again in 3 minutes.", rounded
up, at most a day; "a minute" when no wait was named). A publish result passes a retryable failure's
wait on as `retry_after_seconds`, as the ticket search does. Each client holds at most `ProviderHttp.MaxResponseBytes` (4 MB) of an answer; a larger one is
`Invalid` and not retried. A success that is not JSON, or lacks the id or list the provider reads, is
`Invalid` for a read and `Unknown` for a write, never an exception, and the body is not quoted back.
An empty success lacks the list too: Hudu without a `companies` key is not a tenant with no companies.
Hudu's company list stops at `Hudu:MaxCompanies` (10,000) and is then an error, not a shorter list.

## Public addresses only

A tenant chooses its provider's site, so the ConnectWise and Hudu clients connect only to public
internet addresses (`PublicNetwork`). The check is in the handler's connect step, not only at store
time: it resolves the name once, refuses unless every address is public, and connects to exactly the
addresses it checked, so a name that answers differently a moment later (DNS rebinding) cannot swap in
a private one. The handler has no proxy, follows no redirects and keeps no cookies. A refusal is an
`Invalid` error that names the site as the problem, not an outage, and is not retried. A redirect
answer is the same kind of error, and asks for the site address to be checked in Settings.
`ProviderNetwork:AllowPrivateAddresses` lifts the rule for local development and tests; the service
refuses to start with it outside Development.

## Stand-in servers: the real providers over HTTP (ST-093)

`backend/tests/ScreenTail.Api.Tests/Providers/StandIns/` holds a stand-in ConnectWise and a stand-in
Hudu. Each is a real Kestrel server on a loopback port, over HTTPS with a certificate made for the run,
that keeps what it is sent (notes, time entries, documents, articles, uploads) and records every
request as it arrived. The tests store the stand-ins as a tenant's sites through
`PUT /v1/integrations/{provider}` and drive publish, ticket search, Test connection and the company
mapping endpoints, so every call goes through the real factory, named client, `PublicNetwork` handler,
`ProviderHttp` and provider. No vendor account, key or network is needed.

```bash
export MSBUILDDISABLENODEREUSE=1 DOTNET_CLI_USE_MSBUILD_SERVER=0
dotnet build backend/ScreenTail.Backend.sln
dotnet backend/tests/ScreenTail.Api.Tests/bin/Debug/net10.0/ScreenTail.Api.Tests.dll -class "*OverHttpTests"
```

**How they reach loopback without weakening the address rule.** The test host turns on
`ProviderNetwork:AllowPrivateAddresses`, the switch that exists for tests and Development and that the
service refuses outside Development. The provider clients trust the stand-ins' certificate by its
thumbprint, and only after checking that their handler is the one Program.cs builds (no redirects, no
proxy, the connect-time check). Nothing in the shipped API changes for them.

**What the stand-ins hold the providers to.** ConnectWise: Basic auth that decodes to exactly
`companyId+publicKey:privateKey` with the `clientId` header; ticket search by the `conditions`,
`orderBy`, `fields`, `page` and `pageSize` sent, so a closed ticket, a wrong order or a field not asked
for shows up as a wrong result; a note with text and one of the three note-type flags; a time entry with
a ticket, a start before its end and a known billing option, charged to the API member when it names
none; a document with a ticket and a file. Hudu: the key in `x-api-key` and never in a query string;
companies paged by `page` and `page_size` (28, so two pages); an article wrapped in `article` with a
name and a company that exists; an upload naming an article that exists. Either can be told to answer
the next matching request with a 401, 403, 404, field-level 400 or 422, 429 with `Retry-After`, or 5xx.

**What the tests check.** The note lands on the ticket with the right type and body and its screenshots
as documents, private when the note is; the time entry has the right start, end, billing, text field and
member; the article is a draft under the exactly matched or hand-mapped company (one on the second page
included) with its uploads; a 429 is waited out and sent again when the wait is short, answered at once
as retryable when it is long, saying how long, and given up after three; a 5xx after a write left is
`unknown` and not sent again, while a read is retried; a wrong key is `unauthenticated` with Spec §4's
sentence and never retried; a 403 says to ask an administrator and the note still lands; a field-level
refusal reaches the technician in the provider's own words; and Hudu's "company must exist" for a
company removed while the list was cached forgets the mapping, drops the list and asks for another
company. Every test also
reads the service's own scrubbing log at Trace and fails if anything a provider was sent or answered
appears in it: note text, ticket summaries and numbers, company names, keys, the Basic credential, the
site (INV-10).

**What they prove, and what they cannot.** They prove our side against the published shapes as we read
them: the routes, headers, field names, paging, and what we do with each kind of answer, over a real
socket. They cannot prove that ConnectWise or Hudu accept any of it. The stand-ins' rules are our
reading of the vendors' API references, and their error bodies follow our recorded fixtures, not
captured traffic. In particular, only a real instance can say whether Hudu honours `draft`, which field
names Hudu wants for an upload, whether a board accepts a time entry with no member (the stand-in has a
switch for one that does not), and whether the vendor's front end accepts a JSON body sent chunked,
which is how `ProviderHttp` sends a note, time entry or article (multipart uploads carry a length). The
sandbox check in `docs/integrations/connectwise.md` and the real check in `docs/integrations/hudu.md` are
still owed; when they are done, correct the stand-ins along with the recorded shapes.

## The fakes

`FakePsaProvider` and `FakeDocProvider` exist for two jobs: the contract harness runs against them, so
the rules are executable before the first real connector is written; and the client's publish path
(ST-078) can be finished without a ConnectWise sandbox.

**They are never registered in the application.** `NoFakeProviderIsRegisteredInTheApplication` asserts
the container has none, because a fake PSA in a deployment tells a technician their note was published
when nothing was, and the ticket stays empty until a customer asks why.

## The demo PSA (Development only)

`Demo/DemoProviders.cs` is a PSA and a knowledge base for a live demonstration while there is no
ConnectWise sandbox: seven tickets led by a dental office's offline printer, and three companies with the
same names. It is not the fakes: it has no failure switch, and it is registered, but only when
`Providers:Demo` is on, which the service refuses outside Development. With it on, every tenant's PSA and
knowledge base are the demo, the integrations list names only `demo-psa` and `demo-kb`, and what is
published is kept in memory for the life of the process as text with a count of its screenshots (the
bytes are not kept, INV-7). While it is on, a KB publish matches companies against the demo's in memory
and never touches the tenant's company mappings, and changes to mappings and credentials are refused.
`GET /dev/demo-psa` shows it to a browser on the same machine and to nobody else: loopback at both ends,
no proxy headers, and a local Host name. `docs/dev/demo-psa.md` is the runbook. `ProviderRegistrationTests` and `DemoPsaTests` hold it to
the same rule as the fakes: without the switch, nothing of it is registered or mapped.

## Attachments

`NoteAttachment` carries bytes. Every one has already been through redaction on the technician's machine
before it reached the backend (INV-1), and the backend never writes it anywhere (INV-7).

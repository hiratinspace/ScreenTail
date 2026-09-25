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

Five, and deliberately no more. A taxonomy with thirty entries becomes thirty branches nobody writes, and
each unhandled one falls through to "something went wrong" — the message Spec §4 exists to forbid. These
are the cases where the next step genuinely differs.

| Kind | Means | The technician should |
|---|---|---|
| `Unauthenticated` | Credentials wrong, missing or expired | Fix them in Settings |
| `Forbidden` | Credentials fine, this account may not | Ask an administrator |
| `NotFound` | Ticket, company or article is not there | Pick a different one |
| `Invalid` | The provider refused what we sent | Nothing — it is our bug |
| `Unavailable` | Rate limit, timeout, outage | Wait; this is the only retryable one |

**Only `Unavailable` is retryable**, and `ProviderError.Retryable` is the single place that is decided.
Retrying a wrong API key is how an integration becomes an account lockout; retrying an invalid payload is
the same rejection several hundred times.

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
outage is retried, with exponential backoff and jitter, three attempts at most.

## The fakes

`FakePsaProvider` and `FakeDocProvider` exist for two jobs: the contract harness runs against them, so
the rules are executable before the first real connector is written; and the client's publish path
(ST-078) can be finished without a ConnectWise sandbox.

**They are never registered in the application.** `NoFakeProviderIsRegisteredInTheApplication` asserts
the container has none, because a fake PSA in a deployment tells a technician their note was published
when nothing was, and the ticket stays empty until a customer asks why.

## Attachments

`NoteAttachment` carries bytes. Every one has already been through redaction on the technician's machine
before it reached the backend (INV-1), and the backend never writes it anywhere (INV-7).

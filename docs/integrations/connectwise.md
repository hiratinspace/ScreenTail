# ConnectWise Manage

How ScreenTail talks to ConnectWise PSA (ST-091, ST-092), what a tenant has to supply, and how to check
it against a sandbox before a pilot.

## What ScreenTail does with it

| Call | Route | When |
|---|---|---|
| Check the credential | `GET /system/info` | Settings → Integrations, to show a state rather than "unknown until you publish" |
| Search tickets | `GET /service/tickets/{id}` for a number, then `GET /service/tickets?conditions=summary contains '…' and closedFlag = false&orderBy=dateEntered desc&pageSize=25&page=1` | The Review screen's ticket picker, three characters or a number |
| Add a note | `POST /service/tickets/{id}/notes` with `internalAnalysisFlag` for Internal and `detailDescriptionFlag` for Discussion | Publish |
| Attach screenshots | `POST /system/documents` with `recordType=Ticket`, one per included frame, after the note | Publish |
| Log time | `POST /time/entries` with `chargeToType=ServiceTicket`, a start and an end, `billableOption` | Publish |

Every request carries Basic auth with the tenant's `companyId+publicKey:privateKey` and the vendor
`clientId` header. A 429 or an outage is retried with exponential backoff and jitter, three attempts
at most; a wrong key is not retried, because that is how an integration becomes an account lockout.
Every refusal reaches the technician as a kind and two sentences, with ConnectWise's own words about what
it refused and nothing else from the response (`backend/src/ScreenTail.Api/Providers/README.md`).

## What the tenant supplies

1. **An API member** in ConnectWise: System → Members → API Members. Give it a role that can read
   service tickets and companies, add service notes, add time entries and add documents; nothing more.
2. **API keys for that member**: the member's API Keys tab → New. ConnectWise shows the private key once.
3. **The credential string** ScreenTail stores is the three joined the way ConnectWise's Basic auth
   wants them: `companyId+publicKey:privateKey`, where `companyId` is the login company (the one typed
   on the sign-in screen, lower case).
4. **The site**: the base URL the tenant signs in to, for example `https://na.myconnectwise.net` or
   `https://eu.myconnectwise.net`, or an on-premises host. ScreenTail appends `/v4_6_release/apis/3.0/`.

Stored once, over the device's authenticated connection to the backend, and never returned:

```bash
curl -X PUT "$BACKEND/v1/integrations/connectwise" \
  -H "Authorization: Bearer $SCREENTAIL_DEVICE_TOKEN" -H "Content-Type: application/json" \
  -d '{"siteUrl":"https://na.myconnectwise.net","secret":"acme+PUBLICKEY:PRIVATEKEY"}'
```

`GET /v1/integrations` then shows `connectwise`, the site, and `••••` plus the last four characters.

## What the deployment supplies

**`ConnectWise:ClientId`** — the clientId ConnectWise issues per integration vendor at
developer.connectwise.com. It is ours, not the tenant's, set once per deployment like the vault's master
key. Without it every call is refused before it is sent, with a message naming the setting.

## The sandbox check

Not yet run: it needs a ConnectWise sandbox, which is on the owner's list (`docs/STATUS.md` §5). When
there is one:

1. Create the API member and keys above in the sandbox.
2. Store the credential with the `PUT` above, against a backend running with `ConnectWise:ClientId` set.
3. Search:
   ```bash
   curl "$BACKEND/v1/psa/tickets?q=printer" -H "Authorization: Bearer $SCREENTAIL_DEVICE_TOKEN"
   ```
   Expect open tickets whose summary contains the word, newest first, each with its company. A number
   returns that ticket first. `pr` is refused with `query_too_short`; a wrong key comes back as
   `psa_unauthenticated` with "Update it in Settings → Integrations."
4. Publish (ST-093, ST-094): not wired to the client yet; when it is, the note, its attachments and the
   time entry appear on the ticket and the response carries their ids.

Record what you saw in this file, dated, so the next person knows the recorded shapes in
`ScriptedConnectWise` still match the real API.

## What the recorded shapes are

`backend/tests/ScreenTail.Api.Tests/Providers/ConnectWise/ScriptedConnectWise.cs` answers the routes
above with the fields the provider reads, in the JSON ConnectWise returns them in, written from the API
documentation rather than captured from a sandbox. The contract harness (`PsaProviderContract`) and the
provider's own tests run against it. When the sandbox check has been done, replace what differs.

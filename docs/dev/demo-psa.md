# The demo PSA: showing a note land on a ticket without ConnectWise

**What this is for.** A live demonstration that ends with the drafted note on a ticket, while there is no
ConnectWise sandbox. The backend stands in a PSA and a knowledge base of its own, kept in memory, under
names nobody can take for a real product: **Demo PSA (not ConnectWise)** and **Demo knowledge base (not
Hudu)**. It is the same backend and the same publish path a real PSA gets; only the provider differs.

**What it is not.** Evidence that publishing to ConnectWise works. That still needs the sandbox check in
`docs/integrations/connectwise.md`.

## Switch it on

Development only. The backend refuses to start with the switch on in any other environment, and says
which setting to remove.

Start from the backend shell of `first-draft-end-to-end.md` step 3 (Development, the URLs, the connection
string), then add one line before step 6:

```powershell
$env:Providers__Demo = 'true'
dotnet run --project src\ScreenTail.Api
```

The same thing as an argument, for one run: `dotnet run --project src\ScreenTail.Api -- --Providers:Demo=true`.
To switch it off, remove the variable (`Remove-Item Env:Providers__Demo`) and restart the backend.

While it is on, **every tenant's** PSA and knowledge base are the demo, whatever credentials are stored,
and nothing is sent to ConnectWise or Hudu.

## What the technician sees

- **Settings, Integrations:** a card at the top, *Demo PSA (not ConnectWise)*, Connected, "In memory on
  this backend, Development only", and the same for *Demo knowledge base (not Hudu)*. The ConnectWise and
  Hudu cards say Not connected, which is true: the demo is all a publish reaches. No credential can be
  stored under a `demo-` name.
- **Review, Publish:** a banner above the ticket field: "Publishing to Demo PSA (not ConnectWise): the
  backend keeps the note in memory, and it reaches no real ticket."
- **The ticket picker:** seven tickets. The story is **#48213 Printer offline in reception, Acme Dental**;
  typing `printer`, `48213` or `dental` finds it. The others (Outlook after an update, a scanner, an X-ray
  workstation, a new starter, a VPN, a shared mailbox) are there so the search has something to narrow.
- **After Publish:** "Published to ticket #48213 · 0:30 logged" (the minutes are whatever was chosen). There is no "Open in ConnectWise" button,
  on purpose. A KB article, if ticked, files under Acme Dental without a mapping prompt.

## Showing the note on the ticket

On the laptop, in a browser, before pressing Publish:

```
https://localhost:5001/dev/demo-psa
```

That lists the tickets with how many notes and minutes each has, and the demo knowledge base's articles.
Click **#48213** (`/dev/demo-psa/tickets/48213/view`). The page refreshes every five seconds, so the note
appears on it a moment after Publish: note type, when, how many screenshots were sent, and the note as
the PSA received it, footer included. Time entries are listed below it.

The same as JSON, for a terminal:

```powershell
Invoke-RestMethod https://localhost:5001/dev/demo-psa/tickets/48213 | ConvertTo-Json -Depth 5
```

These pages answer only a browser or terminal on the same machine; from anywhere else they are a 404.
They need no token, like the API document at `/swagger/v1/swagger.json` in Development.

## What it keeps, and for how long

The note's text, the time entry, the article's title and company, and a count of the screenshots, in the
backend's memory until it stops. **The screenshots themselves are not kept** (INV-7): the page says how
many were sent. A restart starts the demo from an empty set of notes.

Company mappings are the one thing written to the database: an article for Acme Dental remembers that
Acme Dental is the demo's company. Connecting a real Hudu clears a tenant's mappings, and a Hudu that was
connected already finds the mapping does not match its own company list and forgets it on the first
publish, as it would any stale mapping.

## Why it cannot reach production

- `Providers:Demo` is refused at startup outside Development (`DemoOptions.Validate`), the way
  `ProviderNetwork:AllowPrivateAddresses` is.
- Without the switch, nothing of the demo is registered and no `/dev` route is mapped:
  `ProviderRegistrationTests` and `DemoPsaTests` check both, in Development and in Production.
- The `/dev/demo-psa` routes are mapped only when the environment is Development and the switch is on,
  and answer only a loopback caller.

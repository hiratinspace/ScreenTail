# Deploying the backend and the web admin on one Linux host

ST-007, the host-agnostic part. `deploy/` runs the API, PostgreSQL and the web admin on any Linux
machine with Docker, served from **one HTTPS origin** (the admin session's cookie needs the page and
`/v1` on the same origin, ADR-0009). No host, domain or mail provider is chosen here: those are the
owner's, and every place that needs one has a placeholder.

```
Internet --443/80--> proxy (Caddy: TLS, the page, /v1 and /health forwarded) --edge--> api --data--> postgres
                                                                                        |
                                                              Google keys, drafting model, tenants' PSAs
```

| Container | Image | Runs as | Network | Ports on the host |
|---|---|---|---|---|
| `proxy` | built from `deploy/web/Dockerfile` (Caddy 2.11.7, the built `web/`) | uid 10001 | `edge` (fixed address) | 80, 443 (tcp and udp), IPv4 only |
| `api` | built from `backend/Dockerfile` (.NET 10 chiseled: no shell) | uid 1654 | `edge`, `data` | none |
| `postgres` | `postgres:17.11-alpine3.24` | uid 70 | `data` (internal: no way out) | none |
| `migrate` | the API image with `--migrate`, run by hand | uid 1654 | `data` | none |

Every container has a read-only root filesystem, no capabilities and `no-new-privileges`. Images are
pinned by version and digest. Secrets are files under `/run/secrets`, never environment variables or
image layers, and each container sees only its own. The API connects to the database as
`screentail_app`, which may read and write rows and nothing else; migrations and backups use the
schema's owner, `screentail`.

## 1. Pick a host and a name (owner decision)

Any Linux machine you can reach over SSH, with **rootful Docker Engine 24 or later and Compose 2.24 or
later** (`docker compose version`), about 2 GB of memory, and **TCP 80 and 443 and UDP 443 open from
the internet** (Let's Encrypt checks TCP 80 and 443; UDP 443 carries HTTP/3; nothing else needs to be
open). Rootless Docker and Podman forward ports through a user-space helper that hides each client's
address, so every client would share one rate-limit count. Pick the name the admin will live at, for
example `admin.yourcompany.example`. Put the host's IPv4 address in DNS as an `A` record for that name
(**no `AAAA` record**: see "What not to do"), and wait until `dig +short admin.yourcompany.example`
answers it.

## 2. Get the code and fill in the two settings files

```bash
git clone https://github.com/hiratinspace/ScreenTail.git && cd ScreenTail/deploy
cp env.example .env           # compose's settings: the name, the client id, ports
cp api.env.example api.env    # the API's settings: signup allowlist, seats, drafting provider
```

Git ignores `.env`, `api.env` and `secrets/`. Neither settings file holds a secret. In `.env` set
`SCREENTAIL_DOMAIN`. In `api.env` set `Admin__SignupAllowlist__0` to the Google address that will
create the first tenant (add `__1`, `__2` for more), and review the ADR-0009 defaults listed there.
Delete a line rather than blanking it: an empty value is not the default.

## 3. Create the Google OAuth client id

As in `docs/dev/web-admin.md` section 1: a **Web application** client whose **Authorized JavaScript
origin** is `https://<SCREENTAIL_DOMAIN>` exactly (no path, no trailing slash), with no redirect URI.
The client secret Google shows is not used anywhere; do not store it. Put the client id in `.env` as
`ADMIN_GOOGLE_CLIENT_ID`: compose gives it to the API and bakes it into the page when the proxy image
is built. Without it the page says sign-in is not configured.

## 4. Make the secrets

```bash
./make-secrets.sh             # writes ./secrets/*; never prints a secret, never replaces one
```

It writes the database passwords, both connection strings, the JWT signing key (48 random bytes; the
API refuses fewer than 32) and the vault's master key (32 random bytes), in a directory only you can
enter. For drafting, put the provider's key in `secrets/summarization-api-key` without echoing it into
your shell history:

```bash
read -rs KEY && printf '%s' "$KEY" > secrets/summarization-api-key && unset KEY
```

Left empty, the API starts and answers drafts with `501 not_configured`. **Back up `secrets/`**
somewhere safe and separate from the database backups: without `vault-master-key` the stored
integration credentials cannot be opened, and nothing can recreate it.

## 5. First start

```bash
docker compose build
docker compose up -d postgres
docker compose run --rm migrate     # "migrations: applied N: ..."
docker compose up -d
docker compose ps                   # postgres, api and proxy "healthy"
curl https://<SCREENTAIL_DOMAIN>/health   # {"status":"ok"}
```

The first start asks Let's Encrypt for the certificate, which takes a few seconds once DNS and port 80
are right; `docker compose logs proxy` says why if not. **Migrations never run on their own**: the
`migrate` service applies the ones compiled into the image you built and exits, and running it twice
is a no-op.

## 6. The first tenant and the first technician

Open `https://<SCREENTAIL_DOMAIN>` and sign in with Google as the allowlisted address. The first sign-in
asks for the organisation's name and creates the tenant with that person as its owner
(`Admin__NewTenantSeats` seats). From then on, **Technicians and seats → Invite a technician** gives a
one-time code to hand on (there is no mail provider yet), which the technician enters in the Windows
client's **Settings → This device**. The client reaches the backend at `https://<SCREENTAIL_DOMAIN>`
(`SCREENTAIL_BACKEND`).

The operator's command line works through the same image:

```bash
docker compose run --rm api --invite new:Acme:5 tech@acme.example   # prints the code once
docker compose run --rm api --set-policy <tenant-id> retention=7
docker compose run --rm api --offboard-tenant <tenant-id>
```

## 7. Updating

```bash
(umask 077 && mkdir -p ~/screentail-backups && docker compose exec -T postgres \
    pg_dump -U screentail -Fc screentail > ~/screentail-backups/backup-$(date +%F).dump)
git pull
docker compose build
docker compose run --rm migrate
docker compose up -d
```

A changed `ADMIN_GOOGLE_CLIENT_ID` or `SCREENTAIL_DOMAIN` needs `docker compose build proxy` and
`docker compose up -d`. To move an image to a newer patch, change its tag and digest together in the
Dockerfile or `compose.yaml`.

## 8. Backups and restore

The database is the volume `screentail_postgres-data`. Back it up with `pg_dump` (above) as often as
you can afford to lose. Write dumps **outside the checkout** with `umask 077`, as above (git also
ignores `deploy/*.dump`), keep copies off the host, and restore one now and then to know it works:

```bash
docker compose stop api proxy
docker compose exec -T postgres pg_restore -U screentail -d screentail --clean --if-exists \
    < ~/screentail-backups/backup-YYYY-MM-DD.dump
docker compose run --rm migrate     # brings an older dump up to this build's schema, and takes the
                                    # API's role's writes on the migration record back
docker compose up -d
```

A restore onto a new host needs the same `secrets/` directory copied there first, before the first
`up` (the API's role and its password are made when the volume is first initialised): then
`docker compose up -d postgres`, the restore and `migrate` as above, then `docker compose up -d`. Keep
backups of `secrets/` readable by their owner only (`umask 077`, files 0600, for example
`(umask 077 && tar -czf ~/screentail-secrets-$(date +%F).tgz -C . secrets)`), and apart from the
database dumps. Caddy's volume `screentail_caddy-data` holds the certificate and ACME account; losing it
only means asking for a new certificate. No frame or capture is ever in the database (INV-7), only
tenants, devices, policy, sealed credentials and counts.

**Rotating the vault's master key** is `docs/security/key-rotation.md`, steps 1 to 5, exactly. Put the
second key in a `compose.override.yaml` beside `compose.yaml` (compose reads it on its own; git does
not track it) rather than editing the tracked file:

```yaml
services:
  api:
    secrets:
      - source: vault-previous-master-key
        target: Vault__PreviousMasterKey
secrets:
  vault-previous-master-key:
    file: ${SCREENTAIL_SECRETS_DIR:-./secrets}/vault-previous-master-key
```

1. Make the new key: `mv secrets/vault-master-key secrets/vault-previous-master-key`, then
   `(umask 077 && openssl rand -base64 32 | tr -d '\n' > secrets/vault-master-key) && chmod 0644 secrets/vault-master-key`.
   **Do not restart the API** (nor run `up`, or reboot) between the `mv` and the override being in
   place: it would start with the new key alone, and every stored credential would be unreadable until
   the previous key is back.
2. and 3. With the override in place, `docker compose up -d api`: the API now has both keys, seals new
   credentials under the new one and still opens rows under the old one.
4. Rewrap: `docker compose run --rm api --rotate-vault-keys`, then run it again; the second run must
   print `rotated 0` (key-rotation.md says what to do if it does not).
5. Remove the override and `secrets/vault-previous-master-key`, then `docker compose up -d api`.

Changing `jwt-signing-key` ends every admin session and every access token at once (devices renew with
their refresh tokens); there is no reason to change it unless it leaked.

`Summarization__FallbackApiKey` is not supplied by this layout: the service has no fallback drafting
provider wired yet. When it does, it goes in the same way as the vault's previous key above, as a
secret file with target `Summarization__FallbackApiKey`, never in `api.env`.

## 9. What not to do

- **Never set `ASPNETCORE_FORWARDEDHEADERS_ENABLED`.** The API trusts `X-Forwarded-For` only from the
  proxy's fixed address (`ForwardedHeaders__KnownProxies__0`, set by compose from `SCREENTAIL_PROXY_IP`)
  and refuses to start with the variable on as well (ADR-0009).
- **Never publish PostgreSQL's port or the API's.** Only the proxy has ports; the `data` network has no
  route out. Reach the database with `docker compose exec postgres psql -U screentail`.
- **Never put a secret in `.env`, `api.env` or `compose.yaml`**, and never commit `secrets/`.
- **Never run the API in Development** on a host: Development reads user secrets and allows the demo
  PSA and the scripted drafting provider, which the API refuses anywhere else for good reason.
- **Never add an `AAAA` record or publish the proxy on IPv6.** The edge network is IPv4 only, so Docker
  would hand every IPv6 connection to Caddy from the network's gateway, and the API would count every
  IPv6 client as that one address: one of them could lock all the others out of sign-in and
  activation. `compose.yaml` publishes on `0.0.0.0` only. Full IPv6 is a follow-up (below).
- **Never set `ForwardedHeaders__KnownNetworks`** to the edge subnet (or anything wider than the
  proxy's one address). The subnet holds Docker's gateway, which is where the host and, with IPv6,
  every IPv6 client come from, so it would trust them to name their own address.
- **Never run `docker compose down -v`** (or `--volumes`): it deletes the database volume and Caddy's
  certificates. `docker compose down` alone keeps them.
- **Never run this under rootless Docker or Podman**: their port forwarding hides each client's
  address (see section 1).
- **Never run the Docker daemon with `"iptables": false`**: every published connection then goes
  through `docker-proxy`, so every client arrives from the edge network's gateway and shares one count.
- **Never keep a copy of `secrets/` readable by anyone else**: back it up with `umask 077`, files 0600.
- Do not turn on Caddy's access log: a request's path can carry text a technician typed (INV-10).
- Do not put another proxy or CDN in front without telling Caddy how to find the real client, or every
  client's address becomes the CDN's and shares one rate-limit count. Plain `trusted_proxies` is not
  enough: Caddy then takes the left-most `X-Forwarded-For` entry, which the client wrote. Use
  `trusted_proxies_strict` with the CDN's ranges, or the CDN's own client-address header (for
  Cloudflare, `client_ip_headers CF-Connecting-IP` with `trusted_proxies` set to Cloudflare's ranges),
  and let only the CDN's ranges reach ports 80 and 443 at the host's firewall.

## 10. Owner decisions still open

- **The host and the name** (above), and whether staging is a second copy of this on another name.
- **A mail provider** for invites and the offboarding receipt (ST-010); until then codes are handed on.
- **ADR-0009's list**: the signup allowlist and seats, session and nonce lifetimes, one rate budget or
  two for an office behind one address (17), IPv6 width (18), binding the nonce to the browser (19),
  more than one API instance (20; this layout runs one).
- **HSTS beyond `max-age=31536000`** (`includeSubDomains`, `preload`), which reaches other names.
- **Backups**: where they go, how often, and who restores one to check.
- **Full IPv6**: an IPv6 subnet on the edge network with a fixed IPv6 address for the proxy, listed as
  `ForwardedHeaders__KnownProxies__1`, then publishing on `[::]` and adding the `AAAA` record.
- **A superuser as the schema owner.** `POSTGRES_USER` (`screentail`), which migrations and backups use,
  is a superuser, as the postgres image makes it. A non-superuser owner of the database and schema would
  narrow what a leaked `migrate-connection` can do.
- **Plaintext between the API and PostgreSQL** on the internal bridge. If PostgreSQL moves off this host
  (a managed database), the connection string needs `SSL Mode=VerifyFull` and the server's CA.
- **No memory or process limits** on the containers (`mem_limit`, `pids_limit`): sized to the host
  once it is chosen.
- **The proxy build's two Alpine packages are not pinned** (`libcap-getcap`, `libcap-setcap`, used only
  to strip a file capability and then removed); the base images are pinned by digest.
- **Processes on the host can reach the API directly** on the edge network, bypassing Caddy (but not
  the forwarded-headers trust, which names only the proxy's address). This layout assumes a host that
  runs nothing else of anybody else's.
- The rest of ST-007's ticket (Terraform for a chosen cloud, managed Postgres, a KMS in place of the
  vault key file, a release-feed bucket for ST-113, a CI deployment with manual approval and the cost
  estimate) waits for the cloud choice; on that day, add the cloud's platform addresses in public ranges
  to `PublicNetwork.NotPublicV4` (the ticket's note).

## How this is checked

`deploy/smoke.sh` builds the images and starts the stack on the machine it runs on, with throwaway
secrets and `localhost` under Caddy's own CA, and checks it from outside: the configuration (only the
proxy publishes a port, on IPv4 only; no network has IPv6; the database network is internal; postgres
logs tersely; nobody is root, writable or capable), that nothing answers on IPv6, the database roles
(the API's role cannot write the migration record or create a table, temporary or not), the page's
headers against `web/security-headers.ts` plus HSTS, that `/health` and `/v1` reach the API and
`/swagger` and `/dev/demo-psa` do not, the migration twice, a device activating through the proxy with
an invite (rows written as `screentail_app`), that a client's own `X-Forwarded-For` is ignored while a
second client is counted as its own address rather than the gateway's, and that the forwarded-headers
switch is refused. CI runs it in the
Backend job whenever `deploy/`, the backend or the page's headers change. The proxy's header snippet is
generated from `web/security-headers.ts` when the image is built (`deploy/web/page-headers.ts`, tested
by `web/src/deployHeaders.test.ts`), so the two cannot drift, and `DeploymentSurfaceTests` holds the
API to mapping nothing outside `/health` and `/v1` in Production.

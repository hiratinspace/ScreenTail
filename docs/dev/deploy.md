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
| `proxy` | built from `deploy/web/Dockerfile` (Caddy 2.11.7, the built `web/`) | uid 10001 | `edge` (fixed address) | 80, 443 (tcp and udp) |
| `api` | built from `backend/Dockerfile` (.NET 10 chiseled: no shell) | uid 1654 | `edge`, `data` | none |
| `postgres` | `postgres:17.11-alpine3.24` | uid 70 | `data` (internal: no way out) | none |
| `migrate` | the API image with `--migrate`, run by hand | uid 1654 | `data` | none |

Every container has a read-only root filesystem, no capabilities and `no-new-privileges`. Images are
pinned by version and digest. Secrets are files under `/run/secrets`, never environment variables or
image layers, and each container sees only its own. The API connects to the database as
`screentail_app`, which may read and write rows and nothing else; migrations and backups use the
schema's owner, `screentail`.

## 1. Pick a host and a name (owner decision)

Any Linux machine you can reach over SSH, with **Docker Engine 24 or later and Compose 2.24 or later**
(`docker compose version`), about 2 GB of memory, and ports **80 and 443 open from the internet**
(Let's Encrypt checks them; nothing else needs to be open). Pick the name the admin will live at, for
example `admin.yourcompany.example`. Put the host's address in DNS as an `A` record for that name (and
`AAAA` if it has IPv6), and wait until `dig +short admin.yourcompany.example` answers it.

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
docker compose exec -T postgres pg_dump -U screentail -Fc screentail > backup-$(date +%F).dump
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
you can afford to lose, keep copies off the host, and restore one now and then to know it works:

```bash
docker compose stop api proxy
docker compose exec -T postgres pg_restore -U screentail -d screentail --clean --if-exists < backup.dump
docker compose up -d
```

A restore onto a new host needs the same `secrets/` directory copied there first, before the first
`up` (the API's role and its password are made when the volume is first initialised). Caddy's volume
`screentail_caddy-data` holds the certificate and ACME account; losing it only means asking for a new
certificate. No frame or capture is ever in the database (INV-7), only tenants, devices, policy,
sealed credentials and counts.

Rotating the vault's master key follows `docs/security/key-rotation.md`: put the old key in a file such
as `secrets/vault-previous-master-key`, add it to the `api` service's secrets with target
`Vault__PreviousMasterKey`, and run `docker compose run --rm api --rotate-vault-keys`. Changing
`jwt-signing-key` ends every admin session and every access token at once (devices renew with their
refresh tokens); there is no reason to change it unless it leaked.

## 9. What not to do

- **Never set `ASPNETCORE_FORWARDEDHEADERS_ENABLED`.** The API trusts `X-Forwarded-For` only from the
  proxy's fixed address (`ForwardedHeaders__KnownProxies__0`, set by compose from `SCREENTAIL_PROXY_IP`)
  and refuses to start with the variable on as well (ADR-0009).
- **Never publish PostgreSQL's port or the API's.** Only the proxy has ports; the `data` network has no
  route out. Reach the database with `docker compose exec postgres psql -U screentail`.
- **Never put a secret in `.env`, `api.env` or `compose.yaml`**, and never commit `secrets/`.
- **Never run the API in Development** on a host: Development reads user secrets and allows the demo
  PSA and the scripted drafting provider, which the API refuses anywhere else for good reason.
- Do not turn on Caddy's access log: a request's path can carry text a technician typed (INV-10).
- Do not put another proxy or CDN in front without telling Caddy (`trusted_proxies` in its global
  options), or every client's address becomes the CDN's and shares one rate-limit count.

## 10. Owner decisions still open

- **The host and the name** (above), and whether staging is a second copy of this on another name.
- **A mail provider** for invites and the offboarding receipt (ST-010); until then codes are handed on.
- **ADR-0009's list**: the signup allowlist and seats, session and nonce lifetimes, one rate budget or
  two for an office behind one address (17), IPv6 width (18), binding the nonce to the browser (19),
  more than one API instance (20; this layout runs one).
- **HSTS beyond `max-age=31536000`** (`includeSubDomains`, `preload`), which reaches other names.
- **Backups**: where they go, how often, and who restores one to check.
- The rest of ST-007's ticket (Terraform for a chosen cloud, managed Postgres, a KMS in place of the
  vault key file, a release-feed bucket for ST-113, a CI deployment with manual approval and the cost
  estimate) waits for the cloud choice; on that day, add the cloud's platform addresses in public ranges
  to `PublicNetwork.NotPublicV4` (the ticket's note).

## How this is checked

`deploy/smoke.sh` builds the images and starts the stack on the machine it runs on, with throwaway
secrets and `localhost` under Caddy's own CA, and checks it from outside: the configuration (only the
proxy publishes a port, the database network is internal, nobody is root, writable or capable), the
page's headers against `web/security-headers.ts` plus HSTS, that `/health` and `/v1` reach the API and
`/swagger` and `/dev/demo-psa` do not, the migration twice, a device activating through the proxy with
an invite (rows written as `screentail_app`), that a client's own `X-Forwarded-For` is ignored while a
second client is counted apart, and that the forwarded-headers switch is refused. CI runs it in the
Backend job whenever `deploy/`, the backend or the page's headers change. The proxy's header snippet is
generated from `web/security-headers.ts` when the image is built (`deploy/web/page-headers.ts`, tested
by `web/src/deployHeaders.test.ts`), so the two cannot drift, and `DeploymentSurfaceTests` holds the
API to mapping nothing outside `/health` and `/v1` in Production.

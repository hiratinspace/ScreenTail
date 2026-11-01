#!/usr/bin/env bash
# The deployment, end to end, on this machine (ST-007). Builds the images, starts PostgreSQL, the API
# and the proxy from deploy/compose.yaml with throwaway secrets and the name `localhost` (Caddy's own
# CA), migrates, and checks from outside what a browser and a device would see:
#
#   - the configuration: only the proxy publishes a port, the database network has no way out, every
#     container is non-root, read-only, without capabilities, and ASPNETCORE_FORWARDEDHEADERS_ENABLED
#     is set nowhere;
#   - the page is served with exactly the headers in web/security-headers.ts, plus HSTS;
#   - /health and /v1 reach the API, and nothing else does: /swagger and /dev/demo-psa are the page's
#     404, not the API's;
#   - the migration step is explicit and runs twice cleanly; a device activates through the proxy with
#     an invite, which writes rows as the API's own database role, and its token works;
#   - X-Forwarded-For is the proxy's and only the proxy's: a client cannot choose its rate-limit
#     partition, and a second client is counted apart from the first;
#   - the API refuses to start with ASPNETCORE_FORWARDEDHEADERS_ENABLED on.
#
# CI runs it in the Backend job. It needs Docker with compose, python3, curl, and ports 80 and 443 free
# (SMOKE_HTTP_PORT and SMOKE_HTTPS_PORT move them). Everything it made is removed when it ends.
set -euo pipefail

here=$(cd "$(dirname "$0")" && pwd)
repo=$(cd "$here/.." && pwd)
scratch=$(mktemp -d "${TMPDIR:-/tmp}/screentail-smoke.XXXXXX")
project="screentail-smoke-$$"
http_port="${SMOKE_HTTP_PORT:-80}"
https_port="${SMOKE_HTTPS_PORT:-443}"
origin="https://localhost:$https_port"
[ "$https_port" = 443 ] && origin="https://localhost"
curl_image="curlimages/curl:8.23.0@sha256:3fb7e13e5d179a845c97d78204f3f1064d1907037f26a3ebf91710571151fd93"

compose() { docker compose -p "$project" -f "$here/compose.yaml" --env-file "$scratch/compose.env" "$@"; }
fail() { echo "::error::deploy smoke: $*" >&2; exit 1; }
pass() { echo "ok      $*"; }
with_timeout() { if command -v timeout >/dev/null 2>&1; then timeout "$@"; else shift; "$@"; fi; }

cleanup() {
    local status=$?
    if [ "$status" -ne 0 ] && [ -f "$scratch/compose.env" ]; then
        compose ps -a || true
        compose logs --no-color --tail 60 || true
    fi
    if [ -f "$scratch/compose.env" ]; then
        compose --profile tools down --volumes --remove-orphans >/dev/null 2>&1 || true
    fi
    rm -rf "$scratch"
    exit "$status"
}
trap cleanup EXIT

# --- The configuration, from the examples the owner copies ------------------------------------------

if grep -Eq '^[[:space:]]*(Jwt__SigningKey|Vault__|Summarization__ApiKey|Summarization__FallbackApiKey|ConnectionStrings__|ASPNETCORE_FORWARDEDHEADERS_ENABLED|ForwardedHeaders__|Admin__GoogleClientId)[[:space:]]*=' "$here/api.env.example"; then
    fail "api.env.example sets a secret, the forwarded-headers switch, or a key compose.yaml owns."
fi
pass "the examples hold no secret"

"$here/make-secrets.sh" "$scratch/secrets" >/dev/null
cp "$here/api.env.example" "$scratch/api.env"
sed -e "s|^SCREENTAIL_DOMAIN=.*|SCREENTAIL_DOMAIN=localhost|" \
    -e "s|^ADMIN_GOOGLE_CLIENT_ID=.*|ADMIN_GOOGLE_CLIENT_ID=smoke-test-placeholder.apps.googleusercontent.com|" \
    -e "s|^SCREENTAIL_HTTP_PORT=.*|SCREENTAIL_HTTP_PORT=$http_port|" \
    -e "s|^SCREENTAIL_HTTPS_PORT=.*|SCREENTAIL_HTTPS_PORT=$https_port|" \
    -e "s|^SCREENTAIL_SECRETS_DIR=.*|SCREENTAIL_SECRETS_DIR=$scratch/secrets|" \
    -e "s|^SCREENTAIL_API_ENV=.*|SCREENTAIL_API_ENV=$scratch/api.env|" \
    "$here/env.example" > "$scratch/compose.env"

compose config --quiet
compose config --format json > "$scratch/config.json"
python3 -I - "$scratch/config.json" <<'EOF'
import json, sys
config = json.load(open(sys.argv[1]))
bad = []
services = config["services"]
for name, service in services.items():
    if name != "proxy" and service.get("ports"):
        bad.append(f"{name} publishes a port; only the proxy may")
    if not service.get("read_only"):
        bad.append(f"{name} has a writable root filesystem")
    if service.get("cap_drop") != ["ALL"] or service.get("cap_add"):
        bad.append(f"{name} keeps a capability")
    if "no-new-privileges:true" not in service.get("security_opt", []):
        bad.append(f"{name} can gain privileges")
    if service.get("privileged") or service.get("network_mode") == "host" or service.get("pid") == "host":
        bad.append(f"{name} shares the host's privileges or namespaces")
    if str(service.get("user", "")).split(":")[0] in ("0", "root"):
        bad.append(f"{name} runs as root")
    if any(k.upper() == "ASPNETCORE_FORWARDEDHEADERS_ENABLED" for k in (service.get("environment") or {})):
        bad.append(f"{name} sets ASPNETCORE_FORWARDEDHEADERS_ENABLED")
if set(services["postgres"].get("networks", {})) != {"data"}:
    bad.append("postgres is on a network other than data")
if not config["networks"]["data"].get("internal"):
    bad.append("the data network has a way out")
if [p.get("target") for p in services["proxy"].get("ports", [])] != [80, 443, 443]:
    bad.append("the proxy publishes something other than 80 and 443")
print("\n".join(bad) if bad else "")
sys.exit(1 if bad else 0)
EOF
pass "only the proxy publishes a port; postgres is on the internal network; no service is root, writable or capable"

# --- Build, migrate, start ---------------------------------------------------------------------------

compose build --quiet
compose up --detach --wait --wait-timeout 120 postgres

first=$(compose --profile tools run --rm -T migrate)
second=$(compose --profile tools run --rm -T migrate)
echo "$first" | grep -q "^migrations: applied " || fail "the first migrate applied nothing: $first"
[ "$second" = "migrations: the database is up to date" ] || fail "the second migrate was not a no-op: $second"
pass "migrate applies the schema once and then has nothing to do"

compose up --detach --wait --wait-timeout 180
pass "api and proxy are healthy"

for id in $(compose ps --quiet); do
    read -r name user readonly <<<"$(docker inspect --format '{{.Name}} {{if .Config.User}}{{.Config.User}}{{else}}-{{end}} {{.HostConfig.ReadonlyRootfs}}' "$id")"
    case "${user%%:*}" in -|0|root) fail "$name runs as root";; esac
    [ "$readonly" = "true" ] || fail "$name has a writable root filesystem"
done
pass "every running container is non-root with a read-only root filesystem"

if compose port postgres 5432 >/dev/null 2>&1 && [ -n "$(compose port postgres 5432 2>/dev/null)" ]; then
    fail "postgres is published on the host"
fi
pass "postgres has no port on the host"

# Caddy's own CA for `localhost`, made as it starts, so the checks below verify the chain rather than
# skipping it.
for _ in $(seq 1 30); do
    compose cp proxy:/data/caddy/pki/authorities/local/root.crt "$scratch/root.crt" >/dev/null 2>&1 && break
    sleep 1
done
[ -s "$scratch/root.crt" ] || fail "Caddy made no local CA for localhost"
fetch() { curl --silent --show-error --cacert "$scratch/root.crt" --max-time 20 "$@"; }
for _ in $(seq 1 30); do
    fetch --output /dev/null "$origin/health" 2>/dev/null && break
    sleep 1
done

# --- The page and its headers ------------------------------------------------------------------------

node_image=$(sed -n 's/^FROM \(node:[^ ]*\) AS build$/\1/p' "$here/web/Dockerfile")
[ -n "$node_image" ] || fail "could not read the node image from deploy/web/Dockerfile"
docker run --rm --network none --read-only -v "$repo/web/security-headers.ts:/repo/web/security-headers.ts:ro" \
    -v "$repo/deploy/web/page-headers.ts:/repo/deploy/web/page-headers.ts:ro" "$node_image" \
    node /repo/deploy/web/page-headers.ts --json > "$scratch/expected.json"

fetch --dump-header "$scratch/page.headers" --output "$scratch/page.html" "$origin/"
fetch --dump-header "$scratch/health.headers" --output "$scratch/health.json" "$origin/health"
for path in /dev/demo-psa/ /swagger/v1/swagger.json /no/such/page; do
    fetch --dump-header "$scratch/404$(echo "$path" | tr '/.' '__').headers" --output /dev/null "$origin$path"
done
asset=$(sed -n 's/.*src="\(\/assets\/[^"]*\.js\)".*/\1/p' "$scratch/page.html" | head -n 1)
[ -n "$asset" ] || fail "the page names no script under /assets"
fetch --dump-header "$scratch/asset.headers" --output /dev/null "$origin$asset"
curl --silent --output /dev/null --dump-header "$scratch/redirect.headers" --max-time 20 "http://localhost:$http_port/"

python3 -I - "$scratch" <<'EOF'
import glob, json, os, sys
scratch = sys.argv[1]
expected = json.load(open(os.path.join(scratch, "expected.json")))
api_csp = "default-src 'none'; frame-ancestors 'none'; base-uri 'none'; form-action 'none'"
hsts = "max-age=31536000"

def read(name):
    lines = open(os.path.join(scratch, name), encoding="latin-1").read().split("\r\n")
    status = int(lines[0].split()[1])
    headers = {}
    for line in lines[1:]:
        if ":" in line:
            key, value = line.split(":", 1)
            headers.setdefault(key.strip().lower(), []).append(value.strip())
    return status, headers

bad = []
def page_headers(name, headers):
    for key, value in expected.items():
        if headers.get(key.lower()) != [value]:
            bad.append(f"{name}: {key} is {headers.get(key.lower())}, expected exactly [{value!r}]")
    if headers.get("strict-transport-security") != [hsts]:
        bad.append(f"{name}: HSTS is {headers.get('strict-transport-security')}")
    if "server" in headers:
        bad.append(f"{name}: names its server")

status, page = read("page.headers")
if status != 200: bad.append(f"/: {status}")
page_headers("/", page)
if page.get("cache-control") != ["no-cache"]: bad.append(f"/: Cache-Control {page.get('cache-control')}")
if '<div id="root">' not in open(os.path.join(scratch, "page.html")).read(): bad.append("/: not the admin page")

status, asset = read("asset.headers")
if status != 200: bad.append(f"asset: {status}")
page_headers("asset", asset)
if asset.get("cache-control") != ["public, max-age=31536000, immutable"]: bad.append(f"asset: Cache-Control {asset.get('cache-control')}")

status, health = read("health.headers")
if status != 200 or json.load(open(os.path.join(scratch, "health.json"))) != {"status": "ok"}:
    bad.append(f"/health: {status}, not the API's answer")
if health.get("content-security-policy") != [api_csp]: bad.append(f"/health: CSP {health.get('content-security-policy')}, not the API's")
if health.get("strict-transport-security") != [hsts]: bad.append(f"/health: HSTS {health.get('strict-transport-security')}")
if "server" in health: bad.append("/health: names its server")

# Answered by the proxy (the page's CSP), never by the API (its closed one).
for name in sorted(glob.glob(os.path.join(scratch, "404*.headers"))):
    name = os.path.basename(name)
    status, headers = read(name)
    if status != 404: bad.append(f"{name}: {status}")
    page_headers(name, headers)

status, redirect = read("redirect.headers")
if status not in (301, 308) or not redirect.get("location", [""])[0].startswith("https://localhost"):
    bad.append(f"http: {status} {redirect.get('location')}, not a redirect to https")

print("\n".join(bad))
sys.exit(1 if bad else 0)
EOF
pass "the page, its assets and its 404s carry exactly web/security-headers.ts and HSTS; /health is the API's; http redirects"

# --- A device, through the proxy, with the API's own database role ----------------------------------

invite=$(compose run --rm -T api --invite new:Smoke:2 smoke@example.com)
code=$(echo "$invite" | sed -n 's/^code[[:space:]]*\([^[:space:]]*\)$/\1/p')
[ -n "$code" ] || fail "--invite printed no code"
activated=$(fetch --fail-with-body -X POST -H 'Content-Type: application/json' \
    --data "{\"code\":\"$code\",\"deviceName\":\"smoke\"}" "$origin/v1/devices/activate")
token=$(printf '%s' "$activated" | python3 -I -c 'import json,sys; print(json.load(sys.stdin)["accessToken"])')
me=$(fetch --fail-with-body -H "Authorization: Bearer $token" "$origin/v1/me")
printf '%s' "$me" | python3 -I -c 'import json,sys; json.load(sys.stdin)' || fail "/v1/me did not answer JSON"
unset code token activated invite
pass "an invite issued with --invite activates a device through the proxy, and its token reads /v1/me"

# --- Whose address the API counts -------------------------------------------------------------------

# From this machine, every call naming a different X-Forwarded-For: the proxy sets that header itself,
# so they all count as this machine and the limit (10 a minute, the device's activation included) is hit.
limited=""
for i in $(seq 1 25); do
    status=$(fetch --output /dev/null --write-out '%{http_code}' -X POST -H "X-Forwarded-For: 198.51.100.$i" "$origin/v1/admin/session/nonce")
    if [ "$status" = 429 ]; then limited=$i; break; fi
    [ "$status" = 200 ] || fail "the sign-in nonce answered $status"
done
[ -n "$limited" ] || fail "twenty-five calls naming twenty-five forwarded addresses were never limited: the client chose its partition"
pass "a client's own X-Forwarded-For is not believed (limited at call $limited)"

# Another client, from another address on the edge network: counted apart, which it is only if the API
# reads the proxy's X-Forwarded-For. With the proxy unlisted, both would be the proxy and share a count.
proxy_ip=$(sed -n 's/^SCREENTAIL_PROXY_IP=//p' "$scratch/compose.env")
other=$(docker run --rm --network "${project}_edge" --read-only --cap-drop ALL --security-opt no-new-privileges:true \
    -v "$scratch/root.crt:/root.crt:ro" "$curl_image" \
    --silent --output /dev/null --write-out '%{http_code}' --cacert /root.crt --max-time 20 \
    --resolve "localhost:443:$proxy_ip" -X POST https://localhost/v1/admin/session/nonce)
[ "$other" = 200 ] || fail "a second client was answered $other: the API is not reading the listed proxy's X-Forwarded-For"
pass "a second client is counted apart from the first"

# --- The switch ADR-0009 says to leave unset -------------------------------------------------------

if refused=$(with_timeout 90 docker compose -p "$project" -f "$here/compose.yaml" --env-file "$scratch/compose.env" \
        run --rm -T --no-deps -e ASPNETCORE_FORWARDEDHEADERS_ENABLED=true api 2>&1); then
    fail "the API started with ASPNETCORE_FORWARDEDHEADERS_ENABLED and a proxy listed"
fi
echo "$refused" | grep -q "ASPNETCORE_FORWARDEDHEADERS_ENABLED is on" || fail "the API stopped, but not for the forwarded-headers switch"
pass "the API refuses to start with ASPNETCORE_FORWARDEDHEADERS_ENABLED on"

echo "deploy smoke: every check passed"

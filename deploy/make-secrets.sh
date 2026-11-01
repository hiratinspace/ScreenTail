#!/bin/sh
# Writes the deployment's secrets as files (ST-007), one per secret, for deploy/compose.yaml to mount
# under /run/secrets. Run once per deployment, on the host:
#
#   ./make-secrets.sh            # into ./secrets
#   ./make-secrets.sh /srv/st    # or another directory
#
# It never prints a secret and never replaces one: a file that exists is kept, because a new vault
# master key would make every stored integration credential unreadable and a new database password
# would no longer match the database. Delete a file on purpose to have it made again.
#
# The directory is 0700, so on the host only its owner (and root) can reach the files; the files are
# 0644 because each container reads its own as a non-root user that is not the host's owner, and
# Docker mounts them with the host's permissions. Back the directory up somewhere safe and apart from
# the database backups (docs/dev/deploy.md, "Backups").
set -eu

dir="${1:-$(dirname "$0")/secrets}"
umask 077
mkdir -p "$dir"
chmod 0700 "$dir"

command -v openssl >/dev/null 2>&1 || { echo "make-secrets.sh needs openssl" >&2; exit 1; }

# Letters and digits only, so a password needs no quoting in a connection string or in SQL.
password() { openssl rand -hex 32; }

made=0
write() {
    # $1 file name, $2 value. Written without a trailing newline, then made readable to the container.
    if [ -e "$dir/$1" ]; then
        echo "kept    $1"
        return
    fi
    printf '%s' "$2" > "$dir/$1"
    chmod 0644 "$dir/$1"
    echo "wrote   $1"
    made=$((made + 1))
}

read_or_make() {
    # The password already on disk (so the connection strings match it), or a new one.
    if [ -e "$dir/$1" ]; then cat "$dir/$1"; else password; fi
}

owner_password=$(read_or_make postgres-password)
app_password=$(read_or_make postgres-app-password)
write postgres-password "$owner_password"
write postgres-app-password "$app_password"

# Both over the internal data network. No Kerberos: the runtime image has no GSSAPI library, and without
# saying so the driver tries to load one on every connection and prints that it could not.
# The schema's owner, for `docker compose run --rm migrate` only.
write migrate-connection "Host=postgres;Port=5432;Database=screentail;Username=screentail;Password=$owner_password;GSS Encryption Mode=Disable"
# The API's role: rows only, no schema changes (postgres/initdb/10-app-role.sh).
write api-connection "Host=postgres;Port=5432;Database=screentail;Username=screentail_app;Password=$app_password;GSS Encryption Mode=Disable"

# 48 random bytes; the API refuses fewer than 32. Signs device tokens, and admin sessions and sign-in
# nonces under keys derived from it (ADR-0009).
write jwt-signing-key "$(openssl rand -base64 48 | tr -d '\n')"
# 32 random bytes, the vault's master key for the tenants' PSA and documentation credentials (ST-009).
write vault-master-key "$(openssl rand -base64 32 | tr -d '\n')"
# The drafting provider's key is the owner's to paste in (backend/README.md, "Drafting"). Empty means
# drafting answers 501 not_configured until it is filled in.
write summarization-api-key ""

echo "$made file(s) written to $dir. No secret was printed."

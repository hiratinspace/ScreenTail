#!/bin/sh
# Run by the postgres image once, when its data volume is first initialised (ST-007). Creates the role
# the API connects as: it may read and write the rows of every table the schema's owner creates, and
# may not create, alter or drop anything, nor make temporary tables. Migrations run as the owner
# (POSTGRES_USER) instead, so a flaw in the API cannot change the schema; `--migrate` also takes every
# write on "__EFMigrationsHistory" back from any other role, so the API cannot forge the record of which
# migrations ran (DatabaseMigrator).
#
# The password is read inside psql from the secret file, so it is never on a command line.
set -eu

secret=/run/secrets/postgres-app-password
case "$(cat "$secret")" in
    '' | *[!A-Za-z0-9]*)
        echo "10-app-role.sh: $secret must hold letters and digits only (make-secrets.sh writes one)." >&2
        exit 1
        ;;
esac

psql -v ON_ERROR_STOP=1 --username "$POSTGRES_USER" --dbname "$POSTGRES_DB" \
    -v owner="$POSTGRES_USER" -v db="$POSTGRES_DB" <<'SQL'
\set app_password `cat /run/secrets/postgres-app-password`
CREATE ROLE screentail_app LOGIN PASSWORD :'app_password' NOSUPERUSER NOCREATEDB NOCREATEROLE NOREPLICATION NOBYPASSRLS;
\unset app_password
REVOKE ALL ON DATABASE :"db" FROM PUBLIC;
GRANT CONNECT ON DATABASE :"db" TO screentail_app;
REVOKE CREATE ON SCHEMA public FROM PUBLIC;
GRANT USAGE ON SCHEMA public TO screentail_app;
ALTER DEFAULT PRIVILEGES FOR ROLE :"owner" IN SCHEMA public GRANT SELECT, INSERT, UPDATE, DELETE ON TABLES TO screentail_app;
ALTER DEFAULT PRIVILEGES FOR ROLE :"owner" IN SCHEMA public GRANT USAGE, SELECT, UPDATE ON SEQUENCES TO screentail_app;
SQL

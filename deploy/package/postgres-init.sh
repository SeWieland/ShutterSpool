#!/bin/sh
set -eu
[ "${POSTGRES_INIT_SKIP_APP:-false}" != true ] || exit 0
psql --username "$POSTGRES_USER" --dbname postgres --set ON_ERROR_STOP=1 <<'SQL'
CREATE DATABASE shutterspool;
CREATE DATABASE keycloak;
SQL

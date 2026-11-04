#!/bin/sh
set -eu
psql --username "$POSTGRES_USER" --dbname postgres --set ON_ERROR_STOP=1 <<'SQL'
CREATE DATABASE keycloak;
SQL

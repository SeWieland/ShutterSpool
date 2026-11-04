\set ON_ERROR_STOP on
\getenv control_runtime_password CONTROL_RUNTIME_PASSWORD
\getenv studio_runtime_password STUDIO_RUNTIME_PASSWORD
\getenv migration_password MIGRATION_PASSWORD
SELECT format('CREATE ROLE shutterspool_control_runtime LOGIN PASSWORD %L NOSUPERUSER NOCREATEDB NOCREATEROLE NOREPLICATION NOINHERIT', :'control_runtime_password')
WHERE NOT EXISTS (SELECT FROM pg_roles WHERE rolname = 'shutterspool_control_runtime') \gexec
SELECT format('CREATE ROLE shutterspool_studio_runtime LOGIN PASSWORD %L NOSUPERUSER NOCREATEDB NOCREATEROLE NOREPLICATION NOINHERIT', :'studio_runtime_password')
WHERE NOT EXISTS (SELECT FROM pg_roles WHERE rolname = 'shutterspool_studio_runtime') \gexec
SELECT format('CREATE ROLE shutterspool_migration LOGIN PASSWORD %L NOSUPERUSER NOCREATEDB NOCREATEROLE NOREPLICATION NOINHERIT', :'migration_password')
WHERE NOT EXISTS (SELECT FROM pg_roles WHERE rolname = 'shutterspool_migration') \gexec
SELECT format('CREATE DATABASE %I OWNER shutterspool_migration', database_name)
FROM (VALUES ('shutterspool_control'), ('shutterspool_studio')) AS databases(database_name)
WHERE NOT EXISTS (SELECT FROM pg_database WHERE datname = database_name) \gexec
DO $$ BEGIN
    IF EXISTS (SELECT FROM pg_database WHERE datname IN ('shutterspool_control', 'shutterspool_studio')
               AND pg_get_userbyid(datdba) <> 'shutterspool_migration') THEN
        RAISE EXCEPTION 'An existing application database has a different owner; select fresh database names and preserve existing state.';
    END IF;
END $$;
REVOKE CONNECT ON DATABASE shutterspool_control FROM PUBLIC;
REVOKE CONNECT ON DATABASE shutterspool_studio FROM PUBLIC;
GRANT CONNECT ON DATABASE shutterspool_control TO shutterspool_control_runtime;
GRANT CONNECT ON DATABASE shutterspool_studio TO shutterspool_studio_runtime;

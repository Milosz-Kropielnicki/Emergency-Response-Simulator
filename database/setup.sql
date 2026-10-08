-- Creates the application role and databases. Run as a PostgreSQL superuser via Setup-Database.ps1,
-- which supplies :app_password. Safe to re-run: existing objects are kept and the password is re-synced.

SELECT format('CREATE ROLE ers_app LOGIN PASSWORD %L', :'app_password')
WHERE NOT EXISTS (SELECT FROM pg_roles WHERE rolname = 'ers_app') \gexec

SELECT format('ALTER ROLE ers_app LOGIN PASSWORD %L', :'app_password') \gexec

SELECT 'CREATE DATABASE ers OWNER ers_app'
WHERE NOT EXISTS (SELECT FROM pg_database WHERE datname = 'ers') \gexec

SELECT 'CREATE DATABASE ers_test OWNER ers_app'
WHERE NOT EXISTS (SELECT FROM pg_database WHERE datname = 'ers_test') \gexec

-- PostGIS is not a trusted extension, so it has to be created by a superuser
-- before the application's migrations run.
\connect ers
CREATE EXTENSION IF NOT EXISTS postgis;

\connect ers_test
CREATE EXTENSION IF NOT EXISTS postgis;

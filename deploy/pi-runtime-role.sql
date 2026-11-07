SELECT format('CREATE ROLE varplats_reader LOGIN PASSWORD %L', :'runtime_password')
WHERE NOT EXISTS (SELECT FROM pg_roles WHERE rolname = 'varplats_reader')
\gexec
REVOKE ALL ON DATABASE varplats FROM PUBLIC;
GRANT CONNECT ON DATABASE varplats TO varplats_reader;
REVOKE CREATE ON SCHEMA public FROM PUBLIC;
GRANT USAGE ON SCHEMA public TO varplats_reader;
GRANT SELECT ON ALL TABLES IN SCHEMA public TO varplats_reader;
ALTER DEFAULT PRIVILEGES FOR ROLE varplats IN SCHEMA public
    GRANT SELECT ON TABLES TO varplats_reader;

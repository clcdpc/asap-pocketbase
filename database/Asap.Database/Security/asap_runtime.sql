CREATE ROLE [asap_runtime];
GO

GRANT SELECT, INSERT, UPDATE, DELETE ON SCHEMA::[asap] TO [asap_runtime];

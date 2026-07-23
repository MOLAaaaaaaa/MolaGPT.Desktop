-- Migrations/008_providers_custom_headers.sql
-- Per-provider custom HTTP headers for BYOK services (parameter overrides).
-- Stored as a JSON array of {"Name":..,"Value":..}; NULL/absent means none, so
-- existing rows keep their current behavior. The migration runner ignores
-- "duplicate column" errors, so re-running on an already-migrated DB is safe.
--
-- Model-level custom body overrides ride inside the existing `models` JSON blob
-- (ProviderModelEntry.CustomBody) and need no schema change.

ALTER TABLE providers ADD COLUMN custom_headers TEXT;

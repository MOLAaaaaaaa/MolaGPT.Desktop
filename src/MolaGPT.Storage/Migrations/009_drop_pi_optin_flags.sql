-- Migrations/009_drop_pi_optin_flags.sql
-- Drop the two opt-in flags that used to pick between the Pi harness and the
-- built-in tool loop. The built-in loop is gone and the agent runtime is the only
-- chat engine, so nothing has read these since; they survive only as rows that
-- make a settings dump look like the choice still exists.
--
-- Rows, not columns, so this is a plain DELETE and is naturally idempotent.

DELETE FROM settings WHERE key IN ('pi.work.enabled', 'pi.byok.enabled');

-- Same story: the locator now resolves the downloaded runtime and nothing else,
-- so a directory or node path named here would be silently ignored. Removing them
-- keeps "what the database says" and "what the app does" in agreement.
DELETE FROM settings WHERE key IN ('pi.work.sidecarDir', 'pi.work.nodePath');

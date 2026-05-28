-- Migrations/002_system_prompt.sql
-- Add system_prompt column to conversations for per-conversation custom prompts.
-- Executed via EnsureSchema which wraps each statement in try/catch for idempotency.

ALTER TABLE conversations ADD COLUMN system_prompt TEXT;


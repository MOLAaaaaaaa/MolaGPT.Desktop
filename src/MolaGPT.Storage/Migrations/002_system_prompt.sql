-- Migrations/002_system_prompt.sql
-- Add system_prompt column to conversations for per-conversation custom prompts.
-- EnsureSchema checks whether the column exists before applying this statement.

ALTER TABLE conversations ADD COLUMN system_prompt TEXT;


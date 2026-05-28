-- Migrations/005_conversations_prompt_mode.sql
-- How the conversation-level system_prompt combines with the persona prompt.
-- 'override' (default, NULL also treated as override) replaces the persona prompt;
-- 'append' concatenates persona prompt + "\n\n" + conversation prompt.

ALTER TABLE conversations ADD COLUMN system_prompt_mode TEXT;

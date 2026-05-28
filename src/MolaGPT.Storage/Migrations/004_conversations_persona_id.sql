-- Migrations/004_conversations_persona_id.sql
-- Bind a conversation to a persona. NULL = no persona attached.

ALTER TABLE conversations ADD COLUMN persona_id TEXT;

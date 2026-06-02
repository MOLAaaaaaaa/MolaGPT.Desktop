-- Migrations/006_providers_purpose.sql
-- Add an explicit `purpose` column so a BYOK provider's use-case (chat vs
-- image generation) is stored independently of its protocol `type`.
-- Replaces the implicit `type == "image-openai"` convention.

ALTER TABLE providers ADD COLUMN purpose TEXT NOT NULL DEFAULT 'chat';

-- Backfill existing image services: the old "image-openai" pseudo-type is
-- split into protocol (openai-compat) + purpose (image). Idempotent.
UPDATE providers SET purpose = 'image', type = 'openai-compat' WHERE type = 'image-openai';

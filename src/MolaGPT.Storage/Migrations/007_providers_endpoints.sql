-- Migrations/007_providers_endpoints.sql
-- Make a BYOK provider's endpoint paths and image dialect explicit and
-- user-editable, instead of hardcoding "/v1/images/generations" etc.
--
--   api_path        chat providers : chat path     (default v1/chat/completions)
--                   image providers: generation path
--   image_edit_path image providers: edit path     (openai-images format only)
--   image_format    image providers: openai-images | openai-chat-image
--
-- All nullable: NULL falls back to the type/format default, so existing rows
-- keep their current behavior. The migration runner ignores "duplicate column"
-- errors, so re-running on an already-migrated DB is safe.

ALTER TABLE providers ADD COLUMN api_path TEXT;
ALTER TABLE providers ADD COLUMN image_edit_path TEXT;
ALTER TABLE providers ADD COLUMN image_format TEXT;

-- Repair image services that were quick-filled for OpenRouter before this
-- change: OpenRouter has no /images/generations endpoint — its image output
-- runs through /chat/completions + modalities. Idempotent.
UPDATE providers
   SET image_format = 'openai-chat-image',
       api_path = 'v1/chat/completions'
 WHERE purpose = 'image'
   AND image_format IS NULL
   AND base_url LIKE '%openrouter.ai%';

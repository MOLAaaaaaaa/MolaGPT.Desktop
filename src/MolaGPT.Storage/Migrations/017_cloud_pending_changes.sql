-- 016's revision guards both local and downloaded writes. Pending changes are
-- local upload work: 1 = metadata/deletion, 2 = messages, 4 = submitted.
ALTER TABLE conversations ADD COLUMN cloud_pending INTEGER;

BEGIN IMMEDIATE;

-- Upgrade from the per-conversation sync marker where one exists. An older
-- global cursor may have passed a change which that conversation never confirmed.
-- Existing tombstones have no durable acknowledgement and can be retried once.
UPDATE conversations
SET cloud_pending = CASE
    WHEN deleted_at IS NOT NULL THEN 1
    WHEN NOT EXISTS (
        SELECT 1 FROM settings WHERE key = 'cloud_sync.conversation_timestamp.' || conversations.id
        AND length(value) > 0
    ) THEN 3
    WHEN updated_at > COALESCE((
        SELECT round(unixepoch(value, 'subsec') * 1000)
        FROM settings WHERE key = 'cloud_sync.conversation_timestamp.' || conversations.id
    ), 0) THEN 1
    ELSE 0
END
WHERE cloud_pending IS NULL AND lower(provider_id) IN ('molagpt-proxy', 'molagpt-local-tools');

DROP TRIGGER IF EXISTS cloud_revision_conversation_insert;
DROP TRIGGER IF EXISTS cloud_revision_conversation_update;
DROP TRIGGER IF EXISTS cloud_revision_message_insert;
DROP TRIGGER IF EXISTS cloud_revision_message_update;
DROP TRIGGER IF EXISTS cloud_revision_message_delete;

CREATE TRIGGER cloud_revision_conversation_insert
AFTER INSERT ON conversations
WHEN lower(NEW.provider_id) IN ('molagpt-proxy', 'molagpt-local-tools')
BEGIN
    UPDATE conversations SET cloud_revision = hex(randomblob(16)), cloud_pending = 1 WHERE id = NEW.id;
END;

CREATE TRIGGER cloud_revision_conversation_update
AFTER UPDATE ON conversations
WHEN NEW.cloud_revision = OLD.cloud_revision AND NEW.cloud_pending IS OLD.cloud_pending
  AND (lower(NEW.provider_id) IN ('molagpt-proxy', 'molagpt-local-tools')
       OR lower(OLD.provider_id) IN ('molagpt-proxy', 'molagpt-local-tools'))
BEGIN
    UPDATE conversations
    SET cloud_revision = hex(randomblob(16)),
        cloud_pending = CASE
            WHEN OLD.deleted_at IS NOT NULL AND NEW.deleted_at IS NULL THEN 3
            WHEN NEW.deleted_at IS NOT OLD.deleted_at OR NEW.title IS NOT OLD.title
                OR NEW.updated_at IS NOT OLD.updated_at OR NEW.pinned IS NOT OLD.pinned
                OR NEW.model_id IS NOT OLD.model_id OR NEW.provider_id IS NOT OLD.provider_id
                THEN (COALESCE(OLD.cloud_pending, 0) & 3) | 1
            WHEN NEW.active_timeline_id IS NOT OLD.active_timeline_id AND OLD.active_timeline_id IS NOT NULL
                THEN (COALESCE(OLD.cloud_pending, 0) & 3) | 2
            ELSE OLD.cloud_pending
        END
    WHERE id = NEW.id;
END;

CREATE TRIGGER cloud_revision_message_insert
AFTER INSERT ON messages
BEGIN
    UPDATE conversations
    SET cloud_revision = hex(randomblob(16)), cloud_pending = (COALESCE(cloud_pending, 0) & 3) | 2
    WHERE id = NEW.conversation_id AND lower(provider_id) IN ('molagpt-proxy', 'molagpt-local-tools');
END;

CREATE TRIGGER cloud_revision_message_update
AFTER UPDATE ON messages
BEGIN
    UPDATE conversations
    SET cloud_revision = hex(randomblob(16)), cloud_pending = (COALESCE(cloud_pending, 0) & 3) | 2
    WHERE id IN (OLD.conversation_id, NEW.conversation_id)
      AND lower(provider_id) IN ('molagpt-proxy', 'molagpt-local-tools');
END;

CREATE TRIGGER cloud_revision_message_delete
AFTER DELETE ON messages
BEGIN
    UPDATE conversations
    SET cloud_revision = hex(randomblob(16)), cloud_pending = (COALESCE(cloud_pending, 0) & 3) | 2
    WHERE id = OLD.conversation_id AND lower(provider_id) IN ('molagpt-proxy', 'molagpt-local-tools');
END;

COMMIT;

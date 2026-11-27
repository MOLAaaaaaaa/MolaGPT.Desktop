ALTER TABLE conversations ADD COLUMN cloud_revision TEXT NOT NULL DEFAULT '';

CREATE TRIGGER IF NOT EXISTS cloud_revision_conversation_insert
AFTER INSERT ON conversations
WHEN NEW.provider_id IN ('molagpt-proxy', 'molagpt-local-tools')
BEGIN
    UPDATE conversations SET cloud_revision = hex(randomblob(16)) WHERE id = NEW.id;
END;

CREATE TRIGGER IF NOT EXISTS cloud_revision_conversation_update
AFTER UPDATE ON conversations
WHEN NEW.cloud_revision = OLD.cloud_revision
  AND (NEW.provider_id IN ('molagpt-proxy', 'molagpt-local-tools')
       OR OLD.provider_id IN ('molagpt-proxy', 'molagpt-local-tools'))
BEGIN
    UPDATE conversations SET cloud_revision = hex(randomblob(16)) WHERE id = NEW.id;
END;

CREATE TRIGGER IF NOT EXISTS cloud_revision_message_insert
AFTER INSERT ON messages
BEGIN
    UPDATE conversations SET cloud_revision = hex(randomblob(16))
    WHERE id = NEW.conversation_id AND provider_id IN ('molagpt-proxy', 'molagpt-local-tools');
END;

CREATE TRIGGER IF NOT EXISTS cloud_revision_message_update
AFTER UPDATE ON messages
BEGIN
    UPDATE conversations SET cloud_revision = hex(randomblob(16))
    WHERE id IN (OLD.conversation_id, NEW.conversation_id)
      AND provider_id IN ('molagpt-proxy', 'molagpt-local-tools');
END;

CREATE TRIGGER IF NOT EXISTS cloud_revision_message_delete
AFTER DELETE ON messages
BEGIN
    UPDATE conversations SET cloud_revision = hex(randomblob(16))
    WHERE id = OLD.conversation_id AND provider_id IN ('molagpt-proxy', 'molagpt-local-tools');
END;

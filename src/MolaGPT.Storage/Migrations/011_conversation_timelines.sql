ALTER TABLE messages ADD COLUMN parent_id TEXT;
ALTER TABLE conversations ADD COLUMN active_timeline_id TEXT;

CREATE TABLE IF NOT EXISTS conversation_timelines (
  id                TEXT PRIMARY KEY,
  conversation_id   TEXT NOT NULL REFERENCES conversations(id) ON DELETE CASCADE,
  leaf_message_id   TEXT,
  role_context_json TEXT NOT NULL DEFAULT '{}',
  created_at        INTEGER NOT NULL,
  updated_at        INTEGER NOT NULL
);

CREATE INDEX IF NOT EXISTS idx_messages_parent
  ON messages(conversation_id, parent_id, created_at);
CREATE INDEX IF NOT EXISTS idx_conversation_timelines_conversation
  ON conversation_timelines(conversation_id, updated_at DESC);

-- Existing conversations are linear. Build their parent chain once, before the
-- first timeline row marks the migration as complete for that conversation.
UPDATE messages AS current
SET parent_id = (
  SELECT previous.id
  FROM messages AS previous
  WHERE previous.conversation_id = current.conversation_id
    AND (previous.created_at < current.created_at
      OR (previous.created_at = current.created_at AND previous.rowid < current.rowid))
  ORDER BY previous.created_at DESC, previous.rowid DESC
  LIMIT 1
)
WHERE NOT EXISTS (
  SELECT 1 FROM conversation_timelines AS timeline
  WHERE timeline.conversation_id = current.conversation_id
);

INSERT INTO conversation_timelines (
  id, conversation_id, leaf_message_id, role_context_json, created_at, updated_at)
SELECT
  conversations.id || ':main',
  conversations.id,
  (
    SELECT messages.id
    FROM messages
    WHERE messages.conversation_id = conversations.id
    ORDER BY messages.created_at DESC, messages.rowid DESC
    LIMIT 1
  ),
  conversations.role_context_json,
  conversations.created_at,
  conversations.updated_at
FROM conversations
WHERE NOT EXISTS (
  SELECT 1 FROM conversation_timelines
  WHERE conversation_timelines.conversation_id = conversations.id
);

UPDATE conversations
SET active_timeline_id = conversations.id || ':main'
WHERE active_timeline_id IS NULL;

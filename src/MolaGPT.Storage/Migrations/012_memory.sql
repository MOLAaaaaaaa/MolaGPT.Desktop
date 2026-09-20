-- Local memory. The entries themselves live in %LocalAppData%\MolaGPT\memory\
-- as Markdown the user can edit; nothing here is a second copy of them.
-- What SQLite owns is the part a text file cannot do: a full-text index over
-- past messages, the tombstones that stop a deleted fact from being relearned,
-- the pending candidates, and how far consolidation has read.

ALTER TABLE conversations ADD COLUMN memory_watermark_at INTEGER NOT NULL DEFAULT 0;
ALTER TABLE conversations ADD COLUMN memory_enabled INTEGER;

-- trigram, not the default tokenizer: the default one does not split Chinese at
-- all. The cost is that queries shorter than three characters never match, so
-- the repository falls back to LIKE for those — see MemoryIndexRepository.
CREATE VIRTUAL TABLE IF NOT EXISTS message_fts USING fts5(
  body,
  message_id UNINDEXED,
  tokenize='trigram'
);

CREATE TABLE IF NOT EXISTS memory_index_state (
  key   TEXT PRIMARY KEY,
  value TEXT NOT NULL
);

-- A fact the user deleted or denied. Keyed by the normalized text so that
-- punctuation alone cannot bring it back under a different key.
CREATE TABLE IF NOT EXISTS memory_suppressions (
  normalized_key TEXT PRIMARY KEY,
  text           TEXT NOT NULL,
  created_at     INTEGER NOT NULL
);

-- Extracted but not confident enough to write into the file unattended.
CREATE TABLE IF NOT EXISTS memory_candidates (
  id              TEXT PRIMARY KEY,
  section         TEXT NOT NULL,
  text            TEXT NOT NULL,
  normalized_key  TEXT NOT NULL,
  quote           TEXT NOT NULL DEFAULT '',
  confidence      REAL NOT NULL DEFAULT 0.5,
  conversation_id TEXT,
  message_id      TEXT,
  created_at      INTEGER NOT NULL
);

CREATE INDEX IF NOT EXISTS idx_memory_candidates_created
  ON memory_candidates(created_at DESC);

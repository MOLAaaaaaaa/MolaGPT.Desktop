-- Migrations/001_init.sql
-- Initial MolaGPT.Desktop SQLite schema. Applied at app startup if schema_version is missing.

CREATE TABLE IF NOT EXISTS conversations (
  id          TEXT PRIMARY KEY,         -- guid
  title       TEXT NOT NULL,
  model_id    TEXT,
  provider_id TEXT,
  created_at  INTEGER NOT NULL,         -- unix ms
  updated_at  INTEGER NOT NULL,         -- unix ms
  pinned      INTEGER NOT NULL DEFAULT 0,
  deleted_at  INTEGER
);

CREATE INDEX IF NOT EXISTS idx_conversations_updated ON conversations(updated_at DESC);

CREATE TABLE IF NOT EXISTS messages (
  id              TEXT PRIMARY KEY,
  conversation_id TEXT NOT NULL REFERENCES conversations(id) ON DELETE CASCADE,
  role            TEXT NOT NULL,        -- system|user|assistant|tool
  content         TEXT NOT NULL,        -- markdown source; assistant content holds full streamed text
  meta            TEXT,                 -- JSON: { model, provider, attachments, usage, finishReason, thinking }
  created_at      INTEGER NOT NULL
);

CREATE INDEX IF NOT EXISTS idx_messages_conv ON messages(conversation_id, created_at);

CREATE TABLE IF NOT EXISTS settings (
  key   TEXT PRIMARY KEY,
  value TEXT NOT NULL
);

CREATE TABLE IF NOT EXISTS providers (
  id          TEXT PRIMARY KEY,         -- guid for BYOK; "molagpt-proxy" reserved
  type        TEXT NOT NULL,            -- openai|anthropic|openai-compat|gemini|molagpt-proxy
  name        TEXT NOT NULL,
  base_url    TEXT,
  api_key_enc BLOB,                     -- DPAPI-encrypted via CredentialStore
  models      TEXT NOT NULL,            -- JSON: [{id, displayName, vision, contextWindow, ...}]
  enabled     INTEGER NOT NULL DEFAULT 1,
  sort_order  INTEGER NOT NULL DEFAULT 0
);

CREATE TABLE IF NOT EXISTS schema_version (version INTEGER PRIMARY KEY);
INSERT OR IGNORE INTO schema_version (version) VALUES (1);

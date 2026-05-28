-- Migrations/003_personas.sql
-- Persona table — first-class system prompt holder.
-- Each persona bundles a name, avatar (emoji), system prompt and optional
-- default tool/thinking switches. Built-in seeds get is_builtin=1 and may
-- not be deleted (can be duplicated and edited).

CREATE TABLE IF NOT EXISTS personas (
  id                       TEXT PRIMARY KEY,
  name                     TEXT NOT NULL,
  avatar                   TEXT,
  system_prompt            TEXT NOT NULL DEFAULT '',
  default_enable_network   INTEGER,
  default_enable_web_fetch INTEGER,
  default_thinking         INTEGER,
  default_reasoning_effort TEXT,
  sort_order               INTEGER NOT NULL DEFAULT 0,
  pinned                   INTEGER NOT NULL DEFAULT 0,
  is_builtin               INTEGER NOT NULL DEFAULT 0,
  created_at               INTEGER NOT NULL,
  updated_at               INTEGER NOT NULL,
  deleted_at               INTEGER
);

CREATE INDEX IF NOT EXISTS idx_personas_sort ON personas(pinned DESC, sort_order ASC, name ASC);

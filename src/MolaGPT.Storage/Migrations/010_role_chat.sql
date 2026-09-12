ALTER TABLE personas ADD COLUMN profile_json TEXT NOT NULL DEFAULT '{}';
ALTER TABLE conversations ADD COLUMN role_context_json TEXT NOT NULL DEFAULT '{}';

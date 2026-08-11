# Prompt 13 — Entity Resolution and Review Inbox

Implement candidate-to-node resolution with component signals: exact/alias match, normalized lexical similarity, optional semantic similarity, source context, confirmed user rules.

Do not collapse everything into an opaque confidence number; persist component signals.

Implement ClarificationTask and Review UI for Same/Different/Not sure, category choice, date interpretation and version relationship.

Implement durable UserResolution rules (aliases, never-merge, locale/category/version mappings), re-evaluate affected candidates, and support merge undo.

Add adversarial fixtures for similar-name but distinct people/projects.

Acceptance: one important ambiguity produces one useful question; resolution affects later imports and can be undone.

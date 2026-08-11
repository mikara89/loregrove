# Prompt 17 — Backup, Restore, and Integrity

Implement library integrity checks and portable archive backup/restore.

Include DB, object store, parsed artifacts and manifest. Exclude credentials and volatile logs.

Create transactionally consistent backups, restore into a new library directory, verify hashes, and add pre-migration backup hook for risky migrations.

Add UI and E2E test: import -> knowledge -> backup -> restore -> compare core state.

Acceptance: the user's library is recoverable without Loregrove cloud services.

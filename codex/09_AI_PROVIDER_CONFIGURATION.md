# Prompt 09 — AI Provider Profiles and Secrets

Implement EmbeddingProfile and ChatProfile persistence without secret values.

Requirements:
- `ISecretStore`;
- secure Windows secret implementation first;
- Settings UI for provider kind/base URL/model/dimensions/key;
- OpenAI-compatible adapter using Microsoft.Extensions.AI where appropriate;
- provider test actions;
- privacy disclosure/acknowledgement;
- redact credentials from logs/errors;
- never start local model processes.

Acceptance: OpenAI-compatible cloud/LAN/localhost endpoints can be configured and tested, while keys are absent from SQLite/logs.

# ADR-009: EF Core SQLite application persistence

Status: Accepted

## Context

Loregrove needs durable local metadata, atomic capture transactions, migrations, and focused use-case
queries. EF Core is not expected to be replaced in the foreseeable future. Repository abstractions
whose only purpose is to mirror `DbSet<T>` and LINQ would add ceremony without meaningful replacement
value.

## Decision

Loregrove intentionally permits EF Core in Application. EF Core is part of the application's
persistence programming model for the foreseeable future. Application owns queries and transaction
orchestration through `ILoregroveDbContext`.

SQLite-specific provider configuration, connections, PRAGMAs, exception inspection, migrations, and
design-time context creation remain in `Loregrove.Infrastructure.Sqlite`.

## Consequences

Positive:

- simpler use cases and direct LINQ;
- fewer unnecessary repositories and mappings;
- transaction ownership remains in Application;
- less abstraction code.

Tradeoffs:

- Application is coupled to EF Core;
- replacing the ORM would require Application changes;
- persistence behavior must be tested with real SQLite rather than EF InMemory.

## Guardrails

- no EF Core in Domain;
- no EF Core in UI;
- no SQLite provider APIs outside Infrastructure.Sqlite;
- no generic repository abstraction;
- database access stays inside Application use cases and query services;
- UI continues through `ILoregroveClient` or another Application facade.

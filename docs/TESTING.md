# Testing

```powershell
dotnet test            # from the repository root
```

The suite needs no SQL Server: service and HTTP tests use real EF Core on SQLite (in-memory or a temp file), so queries, constraints, unique indexes and transactions execute for real. SQL Server-specific objects (procedures, views, triggers) are verified separately with `sqlcmd` (see below).

## Automated tests (`tests/GlasFlow.Tests`, xUnit)

| File | Level | What it proves |
|---|---|---|
| `DomainTests.cs` | Unit | IVA 16% / 8% (border zone) and cent rounding; order totals; invalid quantities; inactive products; order state machine; reserve / ship / adjust rules incl. "no negative stock" and "not below reserved"; low-stock rule; production limits (no over-production, no completion without units); product validation; assistant intent mapping and deterministic summary; CSV round-trip with quotes, commas, newlines and accents |
| `ServiceTests.cs` | Service + real EF Core (SQLite in-memory) | Server-side totals and order numbering; missing customer/product → 404; duplicate external reference → 409; duplicate lines rejected; confirm reserves, ship deducts, movements + audit recorded; **insufficient stock rolls back the whole confirmation (no partial reservation)**; negative stock only when configured; cancel releases reservations; low-stock alert raised once; production completion posts finished goods and movements; over-production leaves stock untouched; accessories cannot be produced; low-stock check idempotent and auto-resolving; transfers; adjustments below zero; duplicate product code; price below cost; ERP import mapping, per-row errors, upsert on re-import, dry run saves nothing, missing columns |
| `AssistantServiceTests.cs` | Service | Fallback without provider, provider receives only the summary, provider failure falls back instead of failing |
| `ApiTests.cs` | Integration (`WebApplicationFactory`, full middleware, SQLite file DB with demo seed) | Public health check, Swagger UI, OpenAPI document and web UI; 401 without token; wrong password; Viewer can read but gets 403 on writes; 400 ProblemDetails with field errors; 404 ProblemDetails; full order flow over HTTP (create → duplicate 409 → confirm → ship → ship again 422); missing customer 404; seeded data feeds dashboard, CSV report and assistant; assistant input validation |

Latest run in this environment (.NET SDK 10.0.403, Windows 11):

```
Passed!  - Failed: 0, Passed: 71, Skipped: 0, Total: 71 - GlasFlow.Tests.dll (net10.0)
```

## Manual verification performed

- API against **SQL Server 2022 Express**: migration applied, seed created (74 orders, 11 production orders), low-stock alerts generated; smoke-tested login, dashboard, products, order create → confirm → ship, invalid transition (422), insufficient stock (422), duplicate reference (409), viewer write (403), production start → register → complete with stock receipt, adjustments, transfers, negative adjustment rejected, CSV export, ERP import (dry run, errors, re-import updates), idempotent inventory check.
- SQL scripts: see `database/README.md` for the sqlcmd verification of procedures, views and triggers.

## Not covered by automated tests

- SQL Server-only behaviour (T-SQL procedures, views, triggers) — verified manually with sqlcmd, not in `dotnet test`.
- Live LLM providers (no API key configured here).
- The web UI and the WPF client have no automated UI tests; see the frontend/desktop notes in the README for what was exercised.

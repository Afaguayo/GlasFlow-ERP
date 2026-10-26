# API documentation

Interactive docs: **http://localhost:5080/swagger** (OpenAPI document at `/openapi/v1.json`).

Conventions
- JSON, camelCase, enums as strings, money in MXN (`decimal`), dates in UTC ISO-8601.
- Auth: `POST /api/auth/login` → `{ token, expiresAt, username, displayName, role }`; send `Authorization: Bearer <token>`. Every endpoint except `/health`, `/openapi`, login and the import template requires a token.
- Roles: **Admin** (everything), **Operator** (all writes except deletes and warehouses), **Viewer** (read-only).
- Lists return `{ items, page, pageSize, totalCount, totalPages }`; `pageSize` is capped at 200.
- Errors are RFC 7807 ProblemDetails: `400` validation (`errors: { Field: [..] }`), `401`, `403`, `404`, `409` conflict/duplicate/concurrency, `422` business rule (e.g. insufficient stock), `429` rate limit, `500` (no internals).

## Endpoints

| Method & route | Role | Description |
|---|---|---|
| `GET /health` | public | Database connectivity check |
| `POST /api/auth/login` | public | Login (rate limited 10/min) |
| `GET /api/auth/me` | any | Current user |
| `GET /api/customers?page&pageSize&search&active` | any | Customers (search name, code, RFC, city) |
| `GET /api/customers/{id}` | any | Customer + total billed + recent orders |
| `POST /api/customers` · `PUT /api/customers/{id}` | writer | Create/update (RFC & postal code validated) |
| `DELETE /api/customers/{id}` | Admin | Delete, or deactivate if it has orders |
| `GET /api/suppliers?search` · `GET /api/suppliers/{id}` | any | Suppliers with product count |
| `POST /api/suppliers` · `PUT …/{id}` · `DELETE …/{id}` | writer / Admin | Maintain suppliers |
| `GET /api/products?page&pageSize&search&category&active&lowStockOnly` | any | Products with total available/reserved and low-stock flag |
| `GET /api/products/categories` | any | Valid categories |
| `GET /api/products/{id}` · `POST` · `PUT /{id}` | any / writer | Read, create (creates stock rows per warehouse), update |
| `DELETE /api/products/{id}` | Admin | Soft delete (deactivate) |
| `GET /api/warehouses` · `POST /api/warehouses` | any / Admin | Warehouses with inventory value |
| `GET /api/inventory?search&category&warehouseId&lowStockOnly&page&pageSize` | any | Stock per product × warehouse |
| `GET /api/inventory/movements?productId&warehouseId&movementType&from&to&page` | any | Movement history |
| `POST /api/inventory/adjustments` | writer | `{productId, warehouseId, quantity (signed), reason}` |
| `POST /api/inventory/transfers` | writer | `{productId, fromWarehouseId, toWarehouseId, quantity, notes}` |
| `POST /api/inventory/check` | writer | Run the low-stock automation now (idempotent) |
| `GET /api/sales-orders?status&customerId&search&from&to&page` | any | Order list |
| `GET /api/sales-orders/{id}` | any | Order with lines |
| `POST /api/sales-orders` | writer | `{customerId, warehouseId, notes, externalReference, lines:[{productId, quantity, unitPrice?}]}` → Draft; totals and IVA server-side; duplicate `externalReference` → 409 |
| `POST /api/sales-orders/{id}/confirm` · `/ship` · `/cancel` | writer | Status transitions with stock reservation / deduction / release |
| `GET /api/production-orders?status&search&page` · `GET …/{id}` | any | Production orders with progress |
| `POST /api/production-orders` | writer | `{productId, warehouseId, quantityPlanned, dueDate, notes}` |
| `POST …/{id}/start` · `/register {quantity}` · `/complete {quantity?}` · `/cancel` | writer | Production workflow |
| `GET /api/dashboard` | any | KPIs, 12-month sales, inventory by category, alerts, recent activity |
| `GET /api/notifications?unreadOnly&includeResolved&take` | any | Notifications |
| `POST /api/notifications/{id}/read` · `/read-all` | any | Mark read |
| `GET /api/reports/{sales\|inventory\|low-stock\|production}` | any | Report dataset + totals (`from`, `to`, `status`, `warehouseId`, `category`) |
| `GET /api/reports/{…}/csv` | any | Same report as UTF-8 CSV |
| `GET /api/reports/views` · `GET /api/reports/views/{name}?top` | any | Rows from whitelisted SQL Server report views (SQL Server only) |
| `POST /api/integration/products/import?dryRun` | writer | Multipart `file` (.csv ≤ 5 MB) → `{totalRows, created, updated, skipped, errors[], dryRun}` |
| `GET /api/integration/products/export` · `/customers/export` | any | CSV exports with external identifiers |
| `GET /api/integration/mapping` | any | Column and category mapping |
| `GET /api/integration/products/template` | public | Sample ERP CSV |
| `GET /api/assistant/suggestions` · `POST /api/assistant/ask {question}` | any | AI business assistant (rate limited 20/min) |
| `GET /api/system/status` | any | App, database, SQL objects, AI and automation status (no secrets) |
| `GET /api/system/audit?take&source` | Admin | Audit trail (`APP`, `DB_TRIGGER`, `DB_PROC`) |

## Example

```bash
TOKEN=$(curl -s -X POST localhost:5080/api/auth/login -H 'Content-Type: application/json' \
  -d '{"username":"operador","password":"<demo password>"}' | jq -r .token)

curl -s -X POST localhost:5080/api/sales-orders -H "Authorization: Bearer $TOKEN" -H 'Content-Type: application/json' \
  -d '{"customerId":2,"warehouseId":2,"externalReference":"PO-778","lines":[{"productId":4,"quantity":10}]}'
# -> 201 {"orderNumber":"SO-2026-0077","status":"Draft","subtotal":9800.00,"taxRate":0.08,"tax":784.00,"total":10584.00,...}

curl -s -X POST localhost:5080/api/sales-orders/77/confirm -H "Authorization: Bearer $TOKEN"
# -> 200 status "Confirmed", or 422 {"detail":"Insufficient stock in warehouse for: ..."}
```

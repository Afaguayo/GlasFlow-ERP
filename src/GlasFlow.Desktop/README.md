# GlasFlow.Desktop

WPF desktop client for **GlasFlow ERP** (portfolio ERP for a fictional glass company in Chihuahua, Mexico). Spanish UI, amounts in MXN (es-MX).

## Purpose and architecture

A deliberately thin client of the GlasFlow REST API. It shows data and sends user intent; it does **not** calculate totals, taxes or stock, and does not validate business rules. The server does that (IVA, stock reservation, field validation) and the client shows the server's answer and error messages.

- **MVVM without third-party packages.** `Infrastructure/Mvvm.cs` holds `ObservableObject`, `RelayCommand`, `AsyncRelayCommand` and `ViewModelBase` (busy counter, error banner, `RunAsync`). Views are XAML `DataTemplate`s selected by view-model type (`Views/*.xaml`); the only code-behind is the login `PasswordBox`.
- **One `ApiClient`** (`Services/ApiClient.cs`) over `HttpClient` + `System.Net.Http.Json`. It stores the JWT after login and sends `Authorization: Bearer`, parses RFC 7807 ProblemDetails (`title`, `detail`, `errors{field:[msg]}`) into `ApiException` messages, reports connection failures as `API no disponible en <url>`, and on HTTP 401 raises `SessionExpired` so the app returns to the login window.
- **Client-side DTO records** in `Services/Dtos.cs`. There are no project references to Api, Application or Infrastructure.
- Everything is async; commands are disabled and a progress bar is shown while a request is running.

```
Views (XAML templates) -> ViewModels -> ApiClient -> REST API
```

## Requirements

- Windows 10/11
- .NET 10 SDK with the Windows Desktop workload (`net10.0-windows`)
- A running GlasFlow API

## Build and run

```
dotnet build src/GlasFlow.Desktop/GlasFlow.Desktop.csproj
dotnet run --project src/GlasFlow.Desktop
```

Demo users (when the API runs with its demo seed): `admin`, `operador`, `consulta`. The password is whatever the API's `Auth:DemoPassword` is set to. Writer actions (adjust stock, create/edit products, create/confirm/ship/cancel orders) need `admin` or `operador`; `consulta` is read-only and the server will answer 403 (the message is shown in the banner).

## Configuration

API base URL, in order of precedence:

1. Environment variable `GLASFLOW_API_URL`
2. `appsettings.json` next to the exe: `{ "ApiUrl": "http://localhost:5080" }` (copied to the output folder)
3. Default `http://localhost:5080`

## Smoke mode (headless)

```
set GLASFLOW_API_URL=http://localhost:5091
set GLASFLOW_USER=admin
set GLASFLOW_PASSWORD=<password>
GlasFlow.Desktop.exe --smoke
```

No window is created. It logs in and calls dashboard, categories, warehouses, suppliers, inventory (all and low-stock), products and sales orders (list plus first detail) through the same `ApiClient` the UI uses. It also sends one invalid order to confirm that ProblemDetails errors are parsed. Output goes to `smoke.log` next to the exe (a WinExe has no console). Exit code `0` = all steps passed, `1` = any failure.

## Screens

| Screen | What it does |
|---|---|
| Login | Username/password; shows API URL and errors (invalid credentials, API unavailable). |
| Tablero | KPI tiles, 12-month sales bar chart (WPF shapes), low-stock list, recent orders, Refresh. |
| Inventario | Stock grid with low-stock highlight; search, category, warehouse, low-stock filters; paging; stock adjustment dialog (signed quantity + reason). |
| Productos | Searchable list; create/edit dialog with server validation errors; deactivate. |
| Pedidos de venta | Filter by status/search, paging; detail pane with lines and server totals; Confirmar / Embarcar / Cancelar enabled by status; "Nuevo pedido" dialog (customer, warehouse, lines) that shows the server-computed subtotal, IVA and total after saving. |

## Testing status (honest)

- Builds with 0 errors and 0 warnings.
- The smoke mode was run against a local API (SQLite) and passed all steps (login, dashboard, catalogs, inventory, products, sales orders, error mapping).
- **Not verified:** the interactive UI was not clicked through. The XAML renders in the sense that it compiles and the app starts to the login window, but layout, the dialogs, status-dependent buttons, the chart and the 401 return-to-login flow have not been exercised by a person or an automated UI test. There are no unit tests in this project.
- Decimal fields in dialogs bind with the es-MX culture; text that cannot be parsed as a number is ignored by WPF binding instead of showing a validation message.

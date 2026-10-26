# Contributing

GlasFlow ERP is a portfolio project, but issues and pull requests are welcome.

1. Fork and create a branch from `main` (`feature/<topic>` or `fix/<topic>`).
2. Keep business rules in `GlasFlow.Domain` / `GlasFlow.Application`; controllers, the web UI and the WPF client stay thin.
3. Model changes need a migration (`dotnet ef migrations add <Name> -p src/GlasFlow.Infrastructure -s src/GlasFlow.Infrastructure`)
   and a regenerated `database/01_schema.sql` (see the header of that file).
4. If you change a stored procedure, view or trigger, keep it consistent with the C# rule it mirrors.
5. Run `dotnet test` before opening a PR; add a test for every bug fix.
6. Never commit secrets, `.env` files or database files.

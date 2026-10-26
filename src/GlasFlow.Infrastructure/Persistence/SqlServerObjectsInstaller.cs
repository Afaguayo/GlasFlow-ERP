using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace GlasFlow.Infrastructure.Persistence;

/// <summary>
/// Deploys the stored procedures, views and triggers from database/03-05 onto the application database
/// (SQL Server provider only). The scripts use CREATE OR ALTER, so running them on every start is idempotent.
/// </summary>
public partial class SqlServerObjectsInstaller(GlasFlowDbContext db, ILogger<SqlServerObjectsInstaller> logger)
{
    public static readonly string[] Scripts = ["03_stored_procedures.sql", "04_views.sql", "05_triggers.sql"];

    public async Task InstallAsync(string scriptDirectory, CancellationToken ct = default)
    {
        foreach (var file in Scripts)
        {
            var path = Path.Combine(scriptDirectory, file);
            if (!File.Exists(path))
            {
                logger.LogWarning("SQL script {File} not found in {Dir}; skipping", file, scriptDirectory);
                continue;
            }
            var batches = GoSeparator().Split(await File.ReadAllTextAsync(path, ct))
                .Where(b => !string.IsNullOrWhiteSpace(b) && !IsUseStatement(b));
            // Plain ADO.NET command: ExecuteSqlRaw would treat {braces} in the scripts as format placeholders.
            var conn = db.Database.GetDbConnection();
            await db.Database.OpenConnectionAsync(ct);
            var count = 0;
            try
            {
                foreach (var batch in batches)
                {
                    await using var cmd = conn.CreateCommand();
                    cmd.CommandText = batch;
                    cmd.CommandTimeout = 120;
                    await cmd.ExecuteNonQueryAsync(ct);
                    count++;
                }
            }
            finally { await db.Database.CloseConnectionAsync(); }
            logger.LogInformation("Applied {File} ({Count} batches)", file, count);
        }
    }

    private static bool IsUseStatement(string batch) =>
        batch.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0 && !l.StartsWith("--")).All(l => l.StartsWith("USE ", StringComparison.OrdinalIgnoreCase));

    [GeneratedRegex(@"^\s*GO\s*;?\s*$", RegexOptions.Multiline | RegexOptions.IgnoreCase)]
    private static partial Regex GoSeparator();
}

using System.Data.Common;
using GlasFlow.Application.Common;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace GlasFlow.Infrastructure.Persistence;

/// <summary>
/// SQL Server only: stores the authenticated application user in SESSION_CONTEXT('UserName') whenever a
/// connection opens, so the audit triggers in database/05_triggers.sql record the real user, not the SQL login.
/// </summary>
public class SessionContextInterceptor(ICurrentUser user) : DbConnectionInterceptor
{
    public override async Task ConnectionOpenedAsync(DbConnection connection, ConnectionEndEventData eventData, CancellationToken ct = default)
    {
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = "EXEC sp_set_session_context @key = N'UserName', @value = @user;";
        var p = cmd.CreateParameter();
        p.ParameterName = "@user";
        p.Value = user.UserName;
        cmd.Parameters.Add(p);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public override void ConnectionOpened(DbConnection connection, ConnectionEndEventData eventData)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "EXEC sp_set_session_context @key = N'UserName', @value = @user;";
        var p = cmd.CreateParameter();
        p.ParameterName = "@user";
        p.Value = user.UserName;
        cmd.Parameters.Add(p);
        cmd.ExecuteNonQuery();
    }
}

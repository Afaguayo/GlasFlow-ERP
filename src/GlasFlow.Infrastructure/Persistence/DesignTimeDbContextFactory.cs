using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace GlasFlow.Infrastructure.Persistence;

/// <summary>Used only by `dotnet ef` to build SQL Server migrations; the connection is never opened for scripting.</summary>
public class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<GlasFlowDbContext>
{
    public GlasFlowDbContext CreateDbContext(string[] args) => new(new DbContextOptionsBuilder<GlasFlowDbContext>()
        .UseSqlServer(Environment.GetEnvironmentVariable("GLASFLOW_DESIGN_CONNECTION")
                      ?? @"Server=.\SQLEXPRESS;Database=GlasFlowERP;Trusted_Connection=True;TrustServerCertificate=True")
        .Options);
}

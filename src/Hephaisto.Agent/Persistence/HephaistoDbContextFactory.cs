using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Hephaisto.Agent.Persistence;

/// <summary>
/// Used only by <c>dotnet ef</c>. Its existence is what lets the migration be generated and
/// scripted with no database anywhere - EF otherwise boots the application's host to find a
/// context, which drags in the Kubernetes client, the LLM client and a real connection.
/// </summary>
/// <remarks>
/// The connection string here is never used to connect during <c>migrations add</c> or
/// <c>migrations script</c>; the provider only needs it to decide how to generate SQL.
/// </remarks>
public sealed class HephaistoDbContextFactory : IDesignTimeDbContextFactory<HephaistoDbContext>
{
    private const string LocalDefault =
        "Host=localhost;Port=5432;Database=hephaisto;Username=hephaisto_app;Password=hephaisto";

    public HephaistoDbContext CreateDbContext(string[] args)
    {
        var connectionString =
            Environment.GetEnvironmentVariable("ConnectionStrings__hephaisto")
            ?? Environment.GetEnvironmentVariable("HEPHAISTO_DB")
            ?? LocalDefault;

        var options = new DbContextOptionsBuilder<HephaistoDbContext>()
            .UseNpgsql(connectionString, o =>
            {
                o.UseVector();
                o.MigrationsAssembly(typeof(HephaistoDbContextFactory).Assembly.FullName);
            })
            .Options;

        return new HephaistoDbContext(options);
    }
}

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Klaxon.Infrastructure.Persistence;

// Used by `dotnet ef` at design time so migrations can be generated without booting the API.
// The connection string is never opened for `migrations add` — it only shapes the model — so a
// localhost default is fine here; runtime uses ConnectionStrings:Postgres from configuration.
internal sealed class KlaxonDbContextFactory : IDesignTimeDbContextFactory<KlaxonDbContext>
{
    public KlaxonDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<KlaxonDbContext>()
            .UseNpgsql(
                "Host=localhost;Database=klaxon;Username=postgres;Password=postgres",
                npgsql => npgsql.UseNodaTime())
            .Options;
        return new KlaxonDbContext(options);
    }
}

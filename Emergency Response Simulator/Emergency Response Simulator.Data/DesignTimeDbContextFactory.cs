using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Microsoft.Extensions.Configuration;

namespace Emergency_Response_Simulator.Data;

/// <summary>Lets <c>dotnet ef</c> build the context without starting the WPF app.</summary>
public sealed class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<ErsDbContext>
{
    public ErsDbContext CreateDbContext(string[] args)
    {
        // "migrations add" never connects, so a placeholder is enough when nothing is configured;
        // "database update" needs the real connection string from appsettings.Local.json.
        var connectionString = ErsConfiguration.Load().GetConnectionString("Ers")
            ?? "Host=localhost;Database=ers;Username=ers_app";

        var options = new DbContextOptionsBuilder<ErsDbContext>();
        ServiceCollectionExtensions.ConfigureErsDb(options, connectionString);
        return new ErsDbContext(options.Options);
    }
}

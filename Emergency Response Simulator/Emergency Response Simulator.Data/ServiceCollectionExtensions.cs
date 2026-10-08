using Emergency_Response_Simulator.Core.Contracts;
using Emergency_Response_Simulator.Core.Events;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Emergency_Response_Simulator.Data;

public static class ServiceCollectionExtensions
{
    /// <summary>Registers the PostgreSQL event store and GIS service.</summary>
    public static IServiceCollection AddErsData(this IServiceCollection services, string connectionString)
    {
        services.AddDbContextFactory<ErsDbContext>(options => ConfigureErsDb(options, connectionString));
        services.AddSingleton<IEventStore, PostgresEventStore>();
        services.AddSingleton<IGisService, GisService>();
        return services;
    }

    public static void ConfigureErsDb(DbContextOptionsBuilder options, string connectionString) =>
        options
            .UseNpgsql(connectionString, npgsql => npgsql.UseNetTopologySuite())
            .UseSnakeCaseNamingConvention();
}

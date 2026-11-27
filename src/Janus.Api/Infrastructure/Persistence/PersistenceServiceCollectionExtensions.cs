using Janus.Api.Configuration;
using Microsoft.EntityFrameworkCore;

namespace Janus.Api.Infrastructure.Persistence;

/// <summary>Enregistrement des services de persistance.</summary>
public static class PersistenceServiceCollectionExtensions
{
    /// <summary>Ajoute le DbContext (rôle runtime) et le point d'interception post-COMMIT.</summary>
    public static IServiceCollection AddJanusPersistence(this IServiceCollection services)
    {
        services.AddDbContext<JanusDbContext>((sp, o) =>
        {
            var cfg = sp.GetRequiredService<IConfiguration>();
            var cmdTimeoutSeconds = ConfigurationReader.GetClampedInt(cfg, "Database:CommandTimeoutSeconds", "DATABASE_COMMAND_TIMEOUT_SECONDS", 10, 1, 120);
            o.UseNpgsql(cfg.GetConnectionString("JanusDb"), npgsql => npgsql.CommandTimeout(cmdTimeoutSeconds));
        });
        services.AddSingleton<JanusTransactionCommitHook>();
        return services;
    }
}

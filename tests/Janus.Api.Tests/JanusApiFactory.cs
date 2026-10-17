using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Janus.Api.Tests;

public class JanusApiFactory : WebApplicationFactory<Program>
{
    public SimulatedTimeProvider SimulatedTime { get; } = new(DateTimeOffset.UtcNow);

    public bool AntiPassbackEnabledOverride { get; set; } = true;
    public int AntiPassbackWindowSecondsOverride { get; set; } = 30;

    public string TerminalToken => Environment.GetEnvironmentVariable("TERMINAL_DEMO_TOKEN") ?? "dev-terminal-token-change-me";
    public string Terminal02Token => Environment.GetEnvironmentVariable("TERMINAL_02_TOKEN") ?? "dev-terminal-02-token-change-me";
    public string AdminToken => Environment.GetEnvironmentVariable("ADMIN_DEMO_TOKEN") ?? "dev-admin-token-change-me";
    public string ScrapeToken => Environment.GetEnvironmentVariable("METRICS_SCRAPE_TOKEN") ?? "dev-metrics-scrape-token-change-me";

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.ConfigureAppConfiguration((context, config) =>
        {
            var dbConn = Environment.GetEnvironmentVariable("ConnectionStrings__JanusDb")
                ?? (Environment.GetEnvironmentVariable("POSTGRES_DB") != null
                    ? $"Host=localhost;Port=5433;Database={Environment.GetEnvironmentVariable("POSTGRES_DB")};Username={Environment.GetEnvironmentVariable("POSTGRES_USER")};Password={Environment.GetEnvironmentVariable("POSTGRES_PASSWORD")}"
                    : "Host=localhost;Port=5433;Database=janus_db;Username=janus_app;Password=local-dev-db-password-change-me");

            var termToken = Environment.GetEnvironmentVariable("TERMINAL_DEMO_TOKEN") ?? "dev-terminal-token-change-me";
            var term2Token = Environment.GetEnvironmentVariable("TERMINAL_02_TOKEN") ?? "dev-terminal-02-token-change-me";
            var adminToken = Environment.GetEnvironmentVariable("ADMIN_DEMO_TOKEN") ?? "dev-admin-token-change-me";
            var scrapeToken = Environment.GetEnvironmentVariable("METRICS_SCRAPE_TOKEN") ?? "dev-metrics-scrape-token-change-me";

            var dict = new Dictionary<string, string?>
            {
                ["ConnectionStrings:JanusDb"] = dbConn,
                ["TerminalAuth:DemoToken"] = termToken,
                ["TerminalAuth:Terminal2Token"] = term2Token,
                ["AdminAuth:DemoToken"] = adminToken,
                ["MetricsAuth:ScrapeToken"] = scrapeToken,
                ["Fare:AmountMillimes"] = "1500",
                ["AntiPassback:Enabled"] = AntiPassbackEnabledOverride ? "true" : "false",
                ["AntiPassback:WindowSeconds"] = AntiPassbackWindowSecondsOverride.ToString()
            };
            config.AddInMemoryCollection(dict);
        });

        builder.ConfigureServices(services =>
        {
            var descriptor = services.SingleOrDefault(d => d.ServiceType == typeof(TimeProvider));
            if (descriptor != null)
            {
                services.Remove(descriptor);
            }
            services.AddSingleton<TimeProvider>(SimulatedTime);
        });
    }

    public JanusDbContext CreateDbContext()
    {
        var scope = Services.CreateScope();
        return scope.ServiceProvider.GetRequiredService<JanusDbContext>();
    }
}

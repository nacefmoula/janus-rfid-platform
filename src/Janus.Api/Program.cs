using Janus.Api.Configuration;
using Janus.Api.Features;
using Janus.Api.Infrastructure.ErrorHandling;
using Janus.Api.Infrastructure.Persistence;
using Janus.Api.Observability;
using Janus.Api.Security;

// Racine de composition : uniquement l'assemblage des services et du pipeline HTTP.
// La logique métier vit dans Janus.Domain et Features/* ; l'infrastructure dans Infrastructure/*.

var builder = WebApplication.CreateBuilder(args);

builder.Services
    .AddSingleton(TimeProvider.System)
    .AddJanusPersistence()
    .AddJanusSecurity()
    .AddJanusObservability();

var app = builder.Build();

// Validation fail-fast de la configuration (secrets, rôles PostgreSQL, tarif) avant toute connexion.
var startupMode = StartupConfigurationValidator.Validate(app.Configuration, app.Environment, args);

// REV-03 : ordre strict du pipeline : ExceptionHandler -> Routing -> ForwardedHeaders -> Authentication -> RateLimiter -> Authorization
app.UseJanusExceptionHandler();
app.UseRouting();
app.UseJanusTrustedForwardedHeaders(app.Configuration);
app.UseAuthentication();
app.UseRateLimiter();
app.UseAuthorization();

app.MapJanusMetrics();

// Mode db-migrator : applique les migrations puis rend un code de sortie (0 / 1) sans démarrer l'API.
var migrationExitCode = await DatabaseMigrationRunner.RunAsync(app, startupMode);
if (migrationExitCode is int exitCode)
{
    return exitCode;
}

// LOT-5A : bornes de démonstration enrôlées par empreinte de jeton (Development uniquement) ; arrêt explicite si incohérence.
if (await DemoTerminalEnrollment.RunAsync(app) is int enrollmentExitCode)
{
    return enrollmentExitCode;
}

app.MapJanusEndpoints(app.Environment, app.Configuration);

await app.RunAsync();
return 0;

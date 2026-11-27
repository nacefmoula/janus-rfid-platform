using Janus.Api.Features.Cards;
using Janus.Api.Features.Dashboard;
using Janus.Api.Features.Health;
using Janus.Api.Features.Organizations;
using Janus.Api.Features.Simulator;
using Janus.Api.Features.Terminals;
using Janus.Api.Features.Validations;
using Janus.Api.Features.Wallet;

namespace Janus.Api.Features;

/// <summary>Point d'entrée unique d'enregistrement des routes métier.</summary>
public static class EndpointRouteBuilderExtensions
{
    /// <summary>Enregistre toutes les fonctionnalités (une classe d'endpoints par dossier Features/*).</summary>
    public static IEndpointRouteBuilder MapJanusEndpoints(this IEndpointRouteBuilder endpoints, IHostEnvironment environment, IConfiguration configuration)
    {
        endpoints.MapHealthEndpoints(environment, configuration);
        endpoints.MapCardsEndpoints();
        endpoints.MapSimulatorEndpoints();
        endpoints.MapDashboardEndpoints();
        endpoints.MapWalletEndpoints();
        endpoints.MapValidationEndpoints();
        endpoints.MapOrganizationEndpoints();
        endpoints.MapTerminalEndpoints();
        return endpoints;
    }
}

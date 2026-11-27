using System.Globalization;
using System.Security.Claims;

namespace Janus.Api.Security;

/// <summary>Claims propres à JANUS portés par le principal authentifié.</summary>
public static class JanusClaims
{
    /// <summary>Identifiant de l'organisation d'une borne.</summary>
    public const string OrganizationId = "janus:organization_id";
    /// <summary>Code de l'organisation d'une borne.</summary>
    public const string OrganizationCode = "janus:organization_code";

    /// <summary>Organisation de la borne authentifiée ; null pour une identité de plateforme (administrateur, collecteur).</summary>
    public static int? GetOrganizationId(ClaimsPrincipal user)
    {
        ArgumentNullException.ThrowIfNull(user);
        var value = user.FindFirstValue(OrganizationId);
        return int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var id) ? id : null;
    }
}

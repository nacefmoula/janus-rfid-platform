using System.Globalization;

namespace Janus.Domain.Terminals;

/// <summary>Durée de validité des jetons de borne (configuration <c>Terminals:TokenLifetimeDays</c>).</summary>
public static class TerminalTokenLifetime
{
    /// <summary>Durée par défaut en jours.</summary>
    public const int DefaultDays = 365;
    /// <summary>Durée maximale acceptée en jours (10 ans).</summary>
    public const int MaxDays = 3650;

    /// <summary>
    /// Interprète la valeur configurée : absente ou vide → <see cref="DefaultDays"/> ; sinon entier dans [1, <see cref="MaxDays"/>].
    /// </summary>
    public static bool TryParse(string? configured, out int days)
    {
        if (string.IsNullOrWhiteSpace(configured))
        {
            days = DefaultDays;
            return true;
        }

        if (int.TryParse(configured.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out days) && days is >= 1 and <= MaxDays)
            return true;

        days = 0;
        return false;
    }

    /// <summary>Durée effective en jours ; la configuration est validée au démarrage, une valeur invalide ici est une erreur de programmation.</summary>
    /// <exception cref="InvalidOperationException">Valeur configurée invalide.</exception>
    public static int ResolveDays(string? configured) =>
        TryParse(configured, out var days)
            ? days
            : throw new InvalidOperationException($"Invalid terminal token lifetime: must be an integer between 1 and {MaxDays} days.");
}

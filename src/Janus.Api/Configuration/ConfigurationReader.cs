namespace Janus.Api.Configuration;

/// <summary>
/// Lecture défensive des paramètres numériques : clé hiérarchique (ex. "Database:LockTimeoutMs") avec repli sur
/// une variable d'environnement historique (ex. "DATABASE_LOCK_TIMEOUT_MS").
/// </summary>
public static class ConfigurationReader
{
    /// <summary>Entier borné dans [min, max] ; valeur par défaut si absent ou illisible.</summary>
    public static int GetClampedInt(IConfiguration config, string primaryKey, string envFallbackKey, int defaultValue, int min, int max)
    {
        var raw = config[primaryKey] ?? config[envFallbackKey];
        return int.TryParse(raw, out var val) ? Math.Clamp(val, min, max) : defaultValue;
    }

    /// <summary>Entier strictement positif ; valeur par défaut si absent, illisible ou non positif.</summary>
    public static int GetPositiveInt(IConfiguration config, string primaryKey, string envFallbackKey, int defaultValue)
    {
        var raw = config[primaryKey] ?? config[envFallbackKey];
        return int.TryParse(raw, out var val) && val > 0 ? val : defaultValue;
    }
}

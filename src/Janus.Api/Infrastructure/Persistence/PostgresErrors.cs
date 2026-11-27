using Npgsql;

namespace Janus.Api.Infrastructure.Persistence;

/// <summary>Classification des erreurs PostgreSQL utilisées par les politiques de résilience.</summary>
public static class PostgresErrors
{
    /// <summary>23505 : violation d'unicité (course d'idempotence ou de création).</summary>
    public const string UniqueViolation = "23505";
    /// <summary>55P03 : lock_timeout dépassé.</summary>
    public const string LockNotAvailable = "55P03";
    /// <summary>57014 : statement_timeout dépassé (requête annulée).</summary>
    public const string QueryCanceled = "57014";
    /// <summary>40001 : échec de sérialisation.</summary>
    public const string SerializationFailure = "40001";
    /// <summary>40P01 : interblocage détecté.</summary>
    public const string DeadlockDetected = "40P01";

    /// <summary>Retourne le SQLSTATE de la première <see cref="PostgresException"/> de la chaîne d'exceptions.</summary>
    public static string? TryGetSqlState(Exception ex)
    {
        for (Exception? curr = ex; curr is not null; curr = curr.InnerException)
        {
            if (curr is PostgresException pgEx)
            {
                return pgEx.SqlState;
            }
        }

        return null;
    }
}

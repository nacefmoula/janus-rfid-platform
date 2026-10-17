using System.Diagnostics.Metrics;

namespace Janus.Api;

public static class JanusMetrics
{
    public const string MeterName = "Janus.Api";

    public static readonly Meter Meter = new(MeterName, "1.0.0");

    /// <summary>
    /// Total number of RFID transport validation attempts by decision, reason, terminal and replay status.
    /// Labels: decision, reason, terminal_id, duplicate (NO high-cardinality card UIDs).
    /// </summary>
    public static readonly Counter<long> ValidationsTotal = Meter.CreateCounter<long>(
        "janus_validations_total",
        unit: "{validation}",
        description: "Nombre total de tentatives de validation billettique");

    /// <summary>
    /// Duration of RFID validation processing in seconds (p50, p95, p99).
    /// Labels: decision.
    /// </summary>
    public static readonly Histogram<double> ValidationDurationSeconds = Meter.CreateHistogram<double>(
        "janus_validation_duration_seconds",
        unit: "s",
        description: "Durée d'exécution du traitement de validation billettique");

    /// <summary>
    /// Cumulative fare revenue debited in millimes.
    /// Labels: terminal_id.
    /// </summary>
    public static readonly Counter<long> FareAmountDebitedMillimesTotal = Meter.CreateCounter<long>(
        "janus_fare_amount_millimes_total",
        unit: "millimes",
        description: "Montant cumulé des débits billettiques en millimes");

    /// <summary>
    /// Total number of failed authentication attempts.
    /// Labels: endpoint, reason (bounded cardinality, NO secrets, tokens, or UIDs).
    /// </summary>
    public static readonly Counter<long> SecurityAuthFailuresTotal = Meter.CreateCounter<long>(
        "janus_security_auth_failures_total",
        unit: "{failure}",
        description: "Nombre total d'échecs d'authentification");

    /// <summary>
    /// Total number of authorization denials.
    /// Labels: endpoint, policy (bounded cardinality).
    /// </summary>
    public static readonly Counter<long> SecurityAuthzDenialsTotal = Meter.CreateCounter<long>(
        "janus_security_authz_denials_total",
        unit: "{denial}",
        description: "Nombre total de refus d'autorisation");

    private static long _conflictValidationsCount;

    public static void RecordConflictValidation() => Interlocked.Increment(ref _conflictValidationsCount);

    public static long GetConflictValidationsCount() => Interlocked.Read(ref _conflictValidationsCount);
}

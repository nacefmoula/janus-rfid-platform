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
}

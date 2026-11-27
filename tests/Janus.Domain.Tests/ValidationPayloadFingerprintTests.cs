using Janus.Domain.Validations;

namespace Janus.Domain.Tests;

public class ValidationPayloadFingerprintTests
{
    private static readonly Guid EventId = Guid.Parse("3f2504e0-4f89-41d3-9a0c-0305e82c3301");
    private static readonly DateTimeOffset OccurredAt = new(2026, 11, 27, 10, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Compute_IsStableAcrossReleases_GoldenValue()
    {
        // Valeur de référence figée : si ce test échoue, l'empreinte a changé et TOUS les rejeux
        // d'événements déjà enregistrés seraient pris pour des conflits d'idempotence (409).
        var hash = ValidationPayloadFingerprint.Compute(EventId, "04A1B2C3D4E5F6", OccurredAt, ValidationEventTypes.TransportValidation);

        // Valeur recalculée indépendamment : SHA-256 de {"eventId":"3f2504e0-…","cardUid":"04A1B2C3D4E5F6","occurredAt":"2026-11-27T10:00:00+00:00","eventType":"TRANSPORT_VALIDATION"}
        Assert.Equal("44AF1D7A623DD7A878451B5F6F48667977D6CFFCC94510F5949A73E98B5CCCDC", hash);
    }

    [Fact]
    public void Compute_IsIndependentOfTheClientUtcOffset()
    {
        var utc = ValidationPayloadFingerprint.Compute(EventId, "04A1", OccurredAt, ValidationEventTypes.TransportValidation);
        var tunis = ValidationPayloadFingerprint.Compute(EventId, "04A1", OccurredAt.ToOffset(TimeSpan.FromHours(1)), ValidationEventTypes.TransportValidation);

        Assert.Equal(utc, tunis);
    }

    [Fact]
    public void Compute_DiffersWhenAnyCanonicalFieldChanges()
    {
        var reference = ValidationPayloadFingerprint.Compute(EventId, "04A1", OccurredAt, ValidationEventTypes.TransportValidation);

        Assert.NotEqual(reference, ValidationPayloadFingerprint.Compute(Guid.NewGuid(), "04A1", OccurredAt, ValidationEventTypes.TransportValidation));
        Assert.NotEqual(reference, ValidationPayloadFingerprint.Compute(EventId, "04A2", OccurredAt, ValidationEventTypes.TransportValidation));
        Assert.NotEqual(reference, ValidationPayloadFingerprint.Compute(EventId, "04A1", OccurredAt.AddSeconds(1), ValidationEventTypes.TransportValidation));
        Assert.NotEqual(reference, ValidationPayloadFingerprint.Compute(EventId, "04A1", null, ValidationEventTypes.TransportValidation));
    }

    [Fact]
    public void Compute_ReturnsUppercaseSha256Hex()
    {
        var hash = ValidationPayloadFingerprint.Compute(EventId, "04A1", null, ValidationEventTypes.TransportValidation);

        Assert.Matches("^[0-9A-F]{64}$", hash);
    }
}

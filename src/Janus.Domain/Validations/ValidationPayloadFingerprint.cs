using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Janus.Domain.Validations;

/// <summary>
/// Empreinte SHA-256 du payload canonique d'un événement : distingue un rejeu exact (même empreinte) d'un conflit
/// d'idempotence (même eventId, payload différent).
/// ATTENTION : le format est contractuel. Le modifier ferait passer pour des conflits les rejeux d'événements déjà enregistrés.
/// </summary>
public static class ValidationPayloadFingerprint
{
    /// <summary>Calcule l'empreinte hexadécimale (majuscules, 64 caractères).</summary>
    public static string Compute(Guid eventId, string normalizedCardUid, DateTimeOffset? occurredAt, string eventType)
    {
        var canonical = JsonSerializer.Serialize(new { eventId, cardUid = normalizedCardUid, occurredAt = occurredAt?.ToUniversalTime(), eventType });
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
    }
}

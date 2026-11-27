namespace Janus.Domain.Validations;

/// <summary>Décision rendue pour un passage de carte (persistée en entier).</summary>
public enum ValidationDecision
{
    /// <summary>Passage accepté (et débité).</summary>
    Accepted = 0,
    /// <summary>Passage refusé, sans débit.</summary>
    Rejected = 1
}

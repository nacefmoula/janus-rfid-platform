namespace Janus.Domain.Cards;

/// <summary>Statut d'exploitation d'une carte (persisté en entier).</summary>
public enum CardStatus
{
    /// <summary>Carte utilisable.</summary>
    Active = 0,
    /// <summary>Carte en opposition : aucune validation ni aucun crédit autorisé.</summary>
    Revoked = 1
}

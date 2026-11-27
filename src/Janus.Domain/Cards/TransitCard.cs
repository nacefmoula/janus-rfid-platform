namespace Janus.Domain.Cards;

/// <summary>Carte émise, avec son solde courant en millimes.</summary>
public sealed class TransitCard
{
    /// <summary>Identifiant technique.</summary>
    public int Id { get; set; }
    /// <summary>UID normalisé (majuscules, sans espaces).</summary>
    public required string Uid { get; set; }
    /// <summary>Statut d'exploitation.</summary>
    public CardStatus Status { get; set; }
    /// <summary>Libellé lisible (porteur, abonnement…).</summary>
    public required string Label { get; set; }
    /// <summary>Solde courant en millimes ; égal à la somme des écritures du journal comptable.</summary>
    public int BalanceMillimes { get; set; }
}

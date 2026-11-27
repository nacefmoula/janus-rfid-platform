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

    /// <summary>Débite la carte. Invariant : le solde ne devient jamais négatif.</summary>
    /// <exception cref="ArgumentOutOfRangeException">Montant non strictement positif.</exception>
    /// <exception cref="InvalidOperationException">Solde insuffisant (la décision métier doit l'avoir écarté avant).</exception>
    public void Debit(int amountMillimes)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(amountMillimes);
        if (BalanceMillimes < amountMillimes)
            throw new InvalidOperationException("Insufficient balance: a debit must never make the balance negative.");
        BalanceMillimes -= amountMillimes;
    }

    /// <summary>Crédite la carte. Invariants : carte active, pas de dépassement de capacité.</summary>
    /// <exception cref="ArgumentOutOfRangeException">Montant non strictement positif.</exception>
    /// <exception cref="InvalidOperationException">Carte révoquée ou capacité dépassée.</exception>
    public void Credit(int amountMillimes)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(amountMillimes);
        if (Status == CardStatus.Revoked)
            throw new InvalidOperationException("Revoked cards cannot be credited.");
        if ((long)BalanceMillimes + amountMillimes > int.MaxValue)
            throw new InvalidOperationException("Resulting balance would exceed the supported maximum.");
        BalanceMillimes += amountMillimes;
    }
}

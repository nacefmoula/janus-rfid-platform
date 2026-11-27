namespace Janus.Domain.Wallet;

/// <summary>
/// Types de mouvements du journal comptable (WalletLedgerEntries). Toute valeur ajoutée ici doit l'être aussi
/// dans la contrainte CHECK CK_WalletLedgerEntries_EntryType (nouvelle migration).
/// </summary>
public static class LedgerEntryTypes
{
    /// <summary>Solde initial d'une carte (création ou reprise des soldes existants lors de la migration).</summary>
    public const string OpeningBalance = "OPENING_BALANCE";
    /// <summary>Débit d'une validation acceptée par un terminal (montant négatif).</summary>
    public const string FareDebit = "FARE_DEBIT";
    /// <summary>Crédit administratif idempotent (recharge, montant positif).</summary>
    public const string AdminCredit = "ADMIN_CREDIT";
    /// <summary>Correction administrative exceptionnelle du solde (delta signé, motif obligatoire côté audit).</summary>
    public const string AdminAdjustment = "ADMIN_ADJUSTMENT";
}

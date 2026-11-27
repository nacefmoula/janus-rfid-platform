using Janus.Domain.Cards;
using Janus.Domain.Wallet;
using Microsoft.EntityFrameworkCore;

namespace Janus.Api.Infrastructure.Persistence;

/// <summary>
/// Seed de démonstration (Development uniquement, REV-07). Chaque solde initial est inscrit au journal comptable
/// afin que l'invariant SUM(WalletLedgerEntries.AmountMillimes) == Cards.BalanceMillimes tienne aussi pour les cartes de démo.
/// </summary>
public static class DemoDataSeeder
{
    /// <summary>Crée les cartes de démonstration si la table Cards est vide.</summary>
    public static async Task SeedDemoCardsIfEmptyAsync(JanusDbContext db)
    {
        ArgumentNullException.ThrowIfNull(db);
        if (await db.Cards.AnyAsync())
        {
            return;
        }

        var demoCards = new[]
        {
            new TransitCard { Uid = "04A1B2C3D4E5F6", Status = CardStatus.Active, Label = "Carte démo active", BalanceMillimes = 10000 },
            new TransitCard { Uid = "04DEAD00000001", Status = CardStatus.Revoked, Label = "Carte démo révoquée", BalanceMillimes = 5000 }
        };

        db.Cards.AddRange(demoCards);
        foreach (var card in demoCards)
        {
            db.WalletLedgerEntries.Add(new WalletLedgerEntry
            {
                Card = card,
                CardUid = card.Uid,
                EntryType = LedgerEntryTypes.OpeningBalance,
                AmountMillimes = card.BalanceMillimes,
                BalanceAfterMillimes = card.BalanceMillimes,
                IdempotencyKey = $"opening:seed:{card.Uid}",
                Actor = "SEED",
                Reason = "Carte de démonstration (Development)",
                CreatedAt = DateTimeOffset.UtcNow
            });
        }

        await db.SaveChangesAsync();
    }
}

using Microsoft.EntityFrameworkCore;
namespace Janus.Api;
/// <summary>
/// Source unique des longueurs de colonnes partagées entre le schéma EF Core et la validation d'entrée de l'API.
/// Modifier une valeur ici impose une nouvelle migration.
/// </summary>
public static class JanusSchemaLimits
{
    public const int CardUidMaxLength = 64;
    public const int CardLabelMaxLength = 120;
    public const int AuditReasonMaxLength = 256;
    public const int LedgerEntryTypeMaxLength = 32;
    public const int LedgerIdempotencyKeyMaxLength = 160;
    public const int LedgerActorMaxLength = 80;
}

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
public enum CardStatus { Active=0, Revoked=1 }
public enum ValidationDecision { Accepted=0, Rejected=1 }
public sealed class TransitCard {
    public int Id { get; set; }
    public required string Uid { get; set; }
    public CardStatus Status { get; set; }
    public required string Label { get; set; }
    public int BalanceMillimes { get; set; }
}
public sealed class ValidationEvent {
    public long Id { get; set; }
    public required string TerminalId { get; set; }
    public Guid EventId { get; set; }
    public required string CardUid { get; set; }
    public required string PayloadHash { get; set; }
    public required string EventType { get; set; }
    public ValidationDecision Decision { get; set; }
    public required string Reason { get; set; }
    public DateTimeOffset? OccurredAt { get; set; }
    public DateTimeOffset ReceivedAt { get; set; }
}
public sealed class FareTransaction {
    public long Id { get; set; }
    public int CardId { get; set; }
    public TransitCard? Card { get; set; }
    public required string CardUid { get; set; }
    public required string TerminalId { get; set; }
    public Guid EventId { get; set; }
    public int AmountMillimes { get; set; }
    public int BalanceAfterMillimes { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}
public sealed class CardAuditLog {
    public long Id { get; set; }
    public required string CardUid { get; set; }
    public required string Action { get; set; }
    public int? PreviousBalanceMillimes { get; set; }
    public int NewBalanceMillimes { get; set; }
    public string? PreviousStatus { get; set; }
    public required string NewStatus { get; set; }
    public string? Reason { get; set; }
    public required string Actor { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}
/// <summary>
/// Écriture du journal comptable : append-only (triggers PostgreSQL), une ligne par mouvement de solde.
/// Invariant : pour chaque carte, SUM(AmountMillimes) == Cards.BalanceMillimes.
/// </summary>
public sealed class WalletLedgerEntry {
    public long Id { get; set; }
    public int CardId { get; set; }
    public TransitCard? Card { get; set; }
    public required string CardUid { get; set; }
    public required string EntryType { get; set; }
    /// <summary>Montant signé en millimes : positif = crédit, négatif = débit. Jamais nul.</summary>
    public int AmountMillimes { get; set; }
    public int BalanceAfterMillimes { get; set; }
    /// <summary>Clé d'idempotence unique du mouvement (ex. "fare:{terminalId}:{eventId}", "credit:{operationId}").</summary>
    public required string IdempotencyKey { get; set; }
    public required string Actor { get; set; }
    public string? Reason { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}
public sealed record ValidationRequest(Guid EventId, string CardUid, DateTimeOffset? OccurredAt, string EventType);
/// <param name="BalanceMillimes">
/// Solde absolu : obligatoire à la création (défaut 0), facultatif sur une carte existante (absent = solde inchangé).
/// Une modification absolue du solde d'une carte existante est une correction exceptionnelle : les recharges passent par
/// POST /api/cards/{uid}/credits.
/// </param>
/// <param name="ExpectedBalanceMillimes">
/// Garde de concurrence optimiste, OBLIGATOIRE pour toute modification absolue du solde d'une carte existante
/// (428 expected_balance_required sinon). Si le solde courant diffère, la mise à jour est refusée (409 stale_balance),
/// ce qui empêche d'écraser un débit survenu depuis la lecture du client.
/// </param>
public sealed record CardUpsertRequest(int? BalanceMillimes, string? Status, string? Label, string? Reason = null, int? ExpectedBalanceMillimes = null);
/// <summary>Crédit idempotent : OperationId est généré par le client et réutilisé tel quel en cas de nouvel essai.</summary>
public sealed record CardCreditRequest(Guid OperationId, int AmountMillimes, string? Reason);
public sealed record LedgerEntryDto(
    long Id,
    string EntryType,
    int AmountMillimes,
    int BalanceAfterMillimes,
    string Actor,
    string? Reason,
    DateTimeOffset CreatedAt
);

public sealed record PagedResult<T>(
    IReadOnlyList<T> Items,
    int Page,
    int PageSize,
    int TotalCount,
    int TotalPages
);

public sealed record AdminTransitCardDto(
    string Uid,
    string Status,
    string Label,
    int BalanceMillimes
);

public sealed record CardAuditLogDto(
    long Id,
    string Action,
    int? PreviousBalanceMillimes,
    int NewBalanceMillimes,
    string? PreviousStatus,
    string NewStatus,
    string? Reason,
    string Actor,
    DateTimeOffset CreatedAt
);
public sealed class JanusDbContext(DbContextOptions<JanusDbContext> options):DbContext(options) {
    public DbSet<TransitCard> Cards => Set<TransitCard>();
    public DbSet<ValidationEvent> Validations => Set<ValidationEvent>();
    public DbSet<FareTransaction> FareTransactions => Set<FareTransaction>();
    public DbSet<CardAuditLog> CardAuditLogs => Set<CardAuditLog>();
    public DbSet<WalletLedgerEntry> WalletLedgerEntries => Set<WalletLedgerEntry>();
    protected override void OnModelCreating(ModelBuilder b) {
        b.Entity<TransitCard>(e => {
            e.HasKey(x => x.Id);
            e.HasIndex(x => x.Uid).IsUnique();
            e.Property(x => x.Uid).HasMaxLength(JanusSchemaLimits.CardUidMaxLength).IsRequired();
            e.Property(x => x.Label).HasMaxLength(JanusSchemaLimits.CardLabelMaxLength).IsRequired();
            e.Property(x => x.BalanceMillimes).HasDefaultValue(0).IsRequired();
            e.ToTable(t => t.HasCheckConstraint("CK_Cards_BalanceMillimes_NonNegative", "\"BalanceMillimes\" >= 0"));
        });
        b.Entity<ValidationEvent>(e => {
            e.HasKey(x => x.Id);
            e.HasIndex(x => new { x.TerminalId, x.EventId }).IsUnique();
            e.HasIndex(x => new { x.TerminalId, x.CardUid, x.Decision, x.ReceivedAt });
            e.Property(x => x.TerminalId).HasMaxLength(80).IsRequired();
            e.Property(x => x.CardUid).HasMaxLength(64).IsRequired();
            e.Property(x => x.PayloadHash).HasMaxLength(64).IsRequired();
            e.Property(x => x.EventType).HasMaxLength(80).IsRequired();
            e.Property(x => x.Reason).HasMaxLength(80).IsRequired();
        });
        b.Entity<FareTransaction>(e => {
            e.HasKey(x => x.Id);
            e.HasIndex(x => new { x.TerminalId, x.EventId }).IsUnique();
            e.HasIndex(x => x.CardId);
            e.Property(x => x.TerminalId).HasMaxLength(80).IsRequired();
            e.Property(x => x.CardUid).HasMaxLength(64).IsRequired();
            e.Property(x => x.AmountMillimes).IsRequired();
            e.Property(x => x.BalanceAfterMillimes).IsRequired();
            e.Property(x => x.CreatedAt).IsRequired();
            e.HasOne(x => x.Card).WithMany().HasForeignKey(x => x.CardId).OnDelete(DeleteBehavior.Restrict);
        });
        b.Entity<CardAuditLog>(e => {
            e.HasKey(x => x.Id);
            e.HasIndex(x => x.CardUid);
            e.Property(x => x.CardUid).HasMaxLength(64).IsRequired();
            e.Property(x => x.Action).HasMaxLength(32).IsRequired();
            e.Property(x => x.NewStatus).HasMaxLength(32).IsRequired();
            e.Property(x => x.PreviousStatus).HasMaxLength(32);
            e.Property(x => x.Reason).HasMaxLength(JanusSchemaLimits.AuditReasonMaxLength);
            e.Property(x => x.Actor).HasMaxLength(64).IsRequired();
            e.Property(x => x.CreatedAt).IsRequired();
        });
        b.Entity<WalletLedgerEntry>(e => {
            e.HasKey(x => x.Id);
            e.HasIndex(x => x.IdempotencyKey).IsUnique();
            e.HasIndex(x => new { x.CardId, x.Id });
            e.Property(x => x.CardUid).HasMaxLength(JanusSchemaLimits.CardUidMaxLength).IsRequired();
            e.Property(x => x.EntryType).HasMaxLength(JanusSchemaLimits.LedgerEntryTypeMaxLength).IsRequired();
            e.Property(x => x.IdempotencyKey).HasMaxLength(JanusSchemaLimits.LedgerIdempotencyKeyMaxLength).IsRequired();
            e.Property(x => x.Actor).HasMaxLength(JanusSchemaLimits.LedgerActorMaxLength).IsRequired();
            e.Property(x => x.Reason).HasMaxLength(JanusSchemaLimits.AuditReasonMaxLength);
            e.Property(x => x.CreatedAt).IsRequired();
            e.HasOne(x => x.Card).WithMany().HasForeignKey(x => x.CardId).OnDelete(DeleteBehavior.Restrict);
            e.ToTable(t => {
                t.HasCheckConstraint("CK_WalletLedgerEntries_AmountNonZero", "\"AmountMillimes\" <> 0");
                t.HasCheckConstraint("CK_WalletLedgerEntries_BalanceAfterNonNegative", "\"BalanceAfterMillimes\" >= 0");
                t.HasCheckConstraint("CK_WalletLedgerEntries_EntryType",
                    "\"EntryType\" IN ('OPENING_BALANCE', 'FARE_DEBIT', 'ADMIN_CREDIT', 'ADMIN_ADJUSTMENT')");
            });
        });
    }
}

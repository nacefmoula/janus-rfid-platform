using Microsoft.EntityFrameworkCore;
namespace Janus.Api;
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
public sealed record ValidationRequest(Guid EventId, string CardUid, DateTimeOffset? OccurredAt, string EventType);
public sealed record CardUpsertRequest(int BalanceMillimes, string? Status, string? Label, string? Reason = null);
public sealed class JanusDbContext(DbContextOptions<JanusDbContext> options):DbContext(options) {
    public DbSet<TransitCard> Cards => Set<TransitCard>();
    public DbSet<ValidationEvent> Validations => Set<ValidationEvent>();
    public DbSet<FareTransaction> FareTransactions => Set<FareTransaction>();
    public DbSet<CardAuditLog> CardAuditLogs => Set<CardAuditLog>();
    protected override void OnModelCreating(ModelBuilder b) {
        b.Entity<TransitCard>(e => {
            e.HasKey(x => x.Id);
            e.HasIndex(x => x.Uid).IsUnique();
            e.Property(x => x.Uid).HasMaxLength(64).IsRequired();
            e.Property(x => x.Label).HasMaxLength(120).IsRequired();
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
            e.Property(x => x.Reason).HasMaxLength(256);
            e.Property(x => x.Actor).HasMaxLength(64).IsRequired();
            e.Property(x => x.CreatedAt).IsRequired();
        });
    }
}

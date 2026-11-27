using Janus.Domain;
using Janus.Domain.Wallet;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Janus.Api.Infrastructure.Persistence.Configurations;

internal sealed class WalletLedgerEntryConfiguration : IEntityTypeConfiguration<WalletLedgerEntry>
{
    public void Configure(EntityTypeBuilder<WalletLedgerEntry> e)
    {
        e.ToTable("WalletLedgerEntries", t =>
        {
            t.HasCheckConstraint("CK_WalletLedgerEntries_AmountNonZero", "\"AmountMillimes\" <> 0");
            t.HasCheckConstraint("CK_WalletLedgerEntries_BalanceAfterNonNegative", "\"BalanceAfterMillimes\" >= 0");
            t.HasCheckConstraint("CK_WalletLedgerEntries_EntryType",
                "\"EntryType\" IN ('OPENING_BALANCE', 'FARE_DEBIT', 'ADMIN_CREDIT', 'ADMIN_ADJUSTMENT')");
        });
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
    }
}

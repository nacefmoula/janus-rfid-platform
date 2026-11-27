using Janus.Domain.Wallet;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Janus.Api.Infrastructure.Persistence.Configurations;

internal sealed class FareTransactionConfiguration : IEntityTypeConfiguration<FareTransaction>
{
    public void Configure(EntityTypeBuilder<FareTransaction> e)
    {
        e.ToTable("FareTransactions");
        e.HasKey(x => x.Id);
        e.HasIndex(x => new { x.TerminalId, x.EventId }).IsUnique();
        e.HasIndex(x => x.CardId);
        e.Property(x => x.TerminalId).HasMaxLength(80).IsRequired();
        e.Property(x => x.CardUid).HasMaxLength(64).IsRequired();
        e.Property(x => x.AmountMillimes).IsRequired();
        e.Property(x => x.BalanceAfterMillimes).IsRequired();
        e.Property(x => x.CreatedAt).IsRequired();
        e.HasOne(x => x.Card).WithMany().HasForeignKey(x => x.CardId).OnDelete(DeleteBehavior.Restrict);
    }
}

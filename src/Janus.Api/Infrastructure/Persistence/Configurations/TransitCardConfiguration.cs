using Janus.Domain;
using Janus.Domain.Cards;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Janus.Api.Infrastructure.Persistence.Configurations;

internal sealed class TransitCardConfiguration : IEntityTypeConfiguration<TransitCard>
{
    public void Configure(EntityTypeBuilder<TransitCard> e)
    {
        e.ToTable("Cards", t => t.HasCheckConstraint("CK_Cards_BalanceMillimes_NonNegative", "\"BalanceMillimes\" >= 0"));
        e.HasKey(x => x.Id);
        e.HasIndex(x => x.Uid).IsUnique();
        e.Property(x => x.Uid).HasMaxLength(JanusSchemaLimits.CardUidMaxLength).IsRequired();
        e.Property(x => x.Label).HasMaxLength(JanusSchemaLimits.CardLabelMaxLength).IsRequired();
        e.Property(x => x.BalanceMillimes).HasDefaultValue(0).IsRequired();
    }
}

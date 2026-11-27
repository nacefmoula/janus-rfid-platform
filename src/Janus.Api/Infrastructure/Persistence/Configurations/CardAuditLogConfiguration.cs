using Janus.Domain;
using Janus.Domain.Organizations;
using Janus.Domain.Cards;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Janus.Api.Infrastructure.Persistence.Configurations;

internal sealed class CardAuditLogConfiguration : IEntityTypeConfiguration<CardAuditLog>
{
    public void Configure(EntityTypeBuilder<CardAuditLog> e)
    {
        e.ToTable("CardAuditLogs");
        e.HasKey(x => x.Id);
        e.HasIndex(x => x.CardUid);
        e.Property(x => x.CardUid).HasMaxLength(64).IsRequired();
        e.Property(x => x.Action).HasMaxLength(32).IsRequired();
        e.Property(x => x.NewStatus).HasMaxLength(32).IsRequired();
        e.Property(x => x.PreviousStatus).HasMaxLength(32);
        e.Property(x => x.Reason).HasMaxLength(JanusSchemaLimits.AuditReasonMaxLength);
        e.Property(x => x.Actor).HasMaxLength(64).IsRequired();
        e.Property(x => x.CreatedAt).IsRequired();
        e.HasIndex(x => x.OrganizationId);
        e.HasOne<Organization>().WithMany().HasForeignKey(x => x.OrganizationId).OnDelete(DeleteBehavior.Restrict);
    }
}

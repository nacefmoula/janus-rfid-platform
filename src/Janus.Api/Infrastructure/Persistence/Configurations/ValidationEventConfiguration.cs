using Janus.Domain.Validations;
using Janus.Domain.Organizations;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Janus.Api.Infrastructure.Persistence.Configurations;

internal sealed class ValidationEventConfiguration : IEntityTypeConfiguration<ValidationEvent>
{
    public void Configure(EntityTypeBuilder<ValidationEvent> e)
    {
        e.ToTable("Validations");
        e.HasKey(x => x.Id);
        e.HasIndex(x => new { x.TerminalId, x.EventId }).IsUnique();
        e.HasIndex(x => new { x.TerminalId, x.CardUid, x.Decision, x.ReceivedAt });
        e.Property(x => x.TerminalId).HasMaxLength(80).IsRequired();
        e.Property(x => x.CardUid).HasMaxLength(64).IsRequired();
        e.Property(x => x.PayloadHash).HasMaxLength(64).IsRequired();
        e.Property(x => x.EventType).HasMaxLength(80).IsRequired();
        e.Property(x => x.Reason).HasMaxLength(80).IsRequired();
        e.HasIndex(x => x.OrganizationId);
        e.HasOne<Organization>().WithMany().HasForeignKey(x => x.OrganizationId).OnDelete(DeleteBehavior.Restrict);
    }
}

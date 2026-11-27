using Janus.Domain;
using Janus.Domain.Organizations;
using Janus.Domain.Terminals;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Janus.Api.Infrastructure.Persistence.Configurations;

internal sealed class TerminalAuditLogConfiguration : IEntityTypeConfiguration<TerminalAuditLog>
{
    public void Configure(EntityTypeBuilder<TerminalAuditLog> e)
    {
        e.ToTable("TerminalAuditLogs", t =>
        {
            t.HasCheckConstraint("CK_TerminalAuditLogs_Action", "\"Action\" IN ('TERMINAL_ENROLLED', 'TERMINAL_REVOKED')");
            t.HasCheckConstraint("CK_TerminalAuditLogs_RevocationReason", "\"Action\" <> 'TERMINAL_REVOKED' OR \"Reason\" IS NOT NULL");
        });
        e.HasKey(x => x.Id);
        e.HasIndex(x => new { x.TerminalId, x.Id });
        e.HasIndex(x => x.OrganizationId);
        e.Property(x => x.TerminalCode).HasMaxLength(JanusSchemaLimits.TerminalCodeMaxLength).IsRequired();
        e.Property(x => x.Action).HasMaxLength(JanusSchemaLimits.AuditActionMaxLength).IsRequired();
        e.Property(x => x.Reason).HasMaxLength(JanusSchemaLimits.AuditReasonMaxLength);
        e.Property(x => x.Actor).HasMaxLength(JanusSchemaLimits.LedgerActorMaxLength).IsRequired();
        e.Property(x => x.CreatedAt).IsRequired();
        e.HasOne<Organization>().WithMany().HasForeignKey(x => x.OrganizationId).OnDelete(DeleteBehavior.Restrict);
        e.HasOne<Terminal>().WithMany().HasForeignKey(x => x.TerminalId).OnDelete(DeleteBehavior.Restrict);
    }
}

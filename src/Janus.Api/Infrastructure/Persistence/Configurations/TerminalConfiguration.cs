using Janus.Domain;
using Janus.Domain.Organizations;
using Janus.Domain.Terminals;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Janus.Api.Infrastructure.Persistence.Configurations;

internal sealed class TerminalConfiguration : IEntityTypeConfiguration<Terminal>
{
    public void Configure(EntityTypeBuilder<Terminal> e)
    {
        e.ToTable("Terminals", t =>
        {
            t.HasCheckConstraint("CK_Terminals_Status", "\"Status\" IN (0, 1)");
            t.HasCheckConstraint("CK_Terminals_Code", "\"Code\" ~ '^[A-Z0-9][A-Z0-9-]{2,79}$'");
            t.HasCheckConstraint("CK_Terminals_TokenHash", "\"TokenHash\" ~ '^[0-9A-F]{64}$'");
            t.HasCheckConstraint("CK_Terminals_ExpiresAfterCreation", "\"ExpiresAt\" > \"CreatedAt\"");
            t.HasCheckConstraint("CK_Terminals_RevocationConsistency",
                "(\"Status\" = 0 AND \"RevokedAt\" IS NULL AND \"RevokedBy\" IS NULL AND \"RevocationReason\" IS NULL) OR " +
                "(\"Status\" = 1 AND \"RevokedAt\" IS NOT NULL AND \"RevokedBy\" IS NOT NULL AND \"RevocationReason\" IS NOT NULL)");
        });
        e.HasKey(x => x.Id);
        e.HasIndex(x => x.Code).IsUnique();
        e.HasIndex(x => x.TokenHash).IsUnique();
        e.HasIndex(x => x.OrganizationId);
        e.Property(x => x.Code).HasMaxLength(JanusSchemaLimits.TerminalCodeMaxLength).IsRequired();
        e.Property(x => x.Label).HasMaxLength(JanusSchemaLimits.TerminalLabelMaxLength).IsRequired();
        e.Property(x => x.TokenHash).HasMaxLength(JanusSchemaLimits.Sha256HexLength).IsRequired();
        e.Property(x => x.ExpiresAt).IsRequired();
        e.Property(x => x.CreatedAt).IsRequired();
        e.Property(x => x.CreatedBy).HasMaxLength(JanusSchemaLimits.LedgerActorMaxLength).IsRequired();
        e.Property(x => x.RevokedBy).HasMaxLength(JanusSchemaLimits.LedgerActorMaxLength);
        e.Property(x => x.RevocationReason).HasMaxLength(JanusSchemaLimits.AuditReasonMaxLength);
        e.HasOne<Organization>().WithMany().HasForeignKey(x => x.OrganizationId).OnDelete(DeleteBehavior.Restrict);
    }
}

using Janus.Domain;
using Janus.Domain.Organizations;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Janus.Api.Infrastructure.Persistence.Configurations;

internal sealed class OrganizationConfiguration : IEntityTypeConfiguration<Organization>
{
    public void Configure(EntityTypeBuilder<Organization> e)
    {
        e.ToTable("Organizations", t =>
        {
            t.HasCheckConstraint("CK_Organizations_Status", "\"Status\" IN (0, 1)");
            t.HasCheckConstraint("CK_Organizations_Code", "\"Code\" ~ '^[A-Z0-9][A-Z0-9-]{1,31}$'");
        });
        e.HasKey(x => x.Id);
        e.HasIndex(x => x.Code).IsUnique();
        e.Property(x => x.Code).HasMaxLength(JanusSchemaLimits.OrganizationCodeMaxLength).IsRequired();
        e.Property(x => x.Name).HasMaxLength(JanusSchemaLimits.OrganizationNameMaxLength).IsRequired();
        e.Property(x => x.CreatedAt).IsRequired();
    }
}

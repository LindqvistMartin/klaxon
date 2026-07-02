using Klaxon.Core.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Klaxon.Infrastructure.Persistence.Configurations;

internal sealed class EscalationLevelConfiguration : IEntityTypeConfiguration<EscalationLevel>
{
    public void Configure(EntityTypeBuilder<EscalationLevel> builder)
    {
        builder.HasKey(x => x.Id);
        // Positions within a policy are unique — two levels cannot share an order slot.
        builder.HasIndex(x => new { x.PolicyId, x.Position }).IsUnique();

        // Targets stored as jsonb (see ADR-004), mapped through the private backing field.
        builder.Property(x => x.Targets)
            .HasColumnName("Targets")
            .HasColumnType("jsonb")
            .HasConversion(JsonbConverters.Converter<EscalationTarget>(), JsonbConverters.Comparer<EscalationTarget>())
            .UsePropertyAccessMode(PropertyAccessMode.Field);
    }
}

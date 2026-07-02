using Klaxon.Core.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Klaxon.Infrastructure.Persistence.Configurations;

internal sealed class ScheduleConfiguration : IEntityTypeConfiguration<Schedule>
{
    public void Configure(EntityTypeBuilder<Schedule> builder)
    {
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Name).HasMaxLength(200).IsRequired();
        builder.Property(x => x.RotationType).HasConversion<string>().HasMaxLength(10).IsRequired();
        builder.Property(x => x.TimeZoneId).HasMaxLength(100).IsRequired();

        // Ordered participant ids stored as jsonb (see ADR-004). Mapped through the private
        // backing field so the entity keeps its read-only IReadOnlyList surface.
        builder.Property(x => x.ParticipantOrder)
            .HasColumnName("ParticipantOrder")
            .HasColumnType("jsonb")
            .HasConversion(JsonbConverters.Converter<Guid>(), JsonbConverters.Comparer<Guid>())
            .UsePropertyAccessMode(PropertyAccessMode.Field);

        builder.HasMany(x => x.Overrides)
            .WithOne(x => x.Schedule)
            .HasForeignKey(x => x.ScheduleId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}

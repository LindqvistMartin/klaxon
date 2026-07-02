using Klaxon.Core.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Klaxon.Infrastructure.Persistence.Configurations;

internal sealed class AlertConfiguration : IEntityTypeConfiguration<Alert>
{
    public void Configure(EntityTypeBuilder<Alert> builder)
    {
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Source).HasMaxLength(100).IsRequired();
        builder.Property(x => x.DedupKey).HasMaxLength(200).IsRequired();
        builder.Property(x => x.Payload).HasColumnType("jsonb").IsRequired();
        builder.Property(x => x.Status).HasConversion<string>().HasMaxLength(10).IsRequired();

        // At most one open alert per (Source, DedupKey): repeated arrivals upsert onto the same
        // row so a flapping alert reuses one escalation instead of storming (see ADR-004). A plain
        // index without the filter would forbid a fresh alert after the first one resolves.
        builder.HasIndex(x => new { x.Source, x.DedupKey })
            .HasDatabaseName("IX_Alerts_OpenDedup")
            .IsUnique()
            .HasFilter("""("Status" = 'Open')""");
    }
}

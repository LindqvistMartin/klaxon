using Klaxon.Core.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Klaxon.Infrastructure.Persistence.Configurations;

internal sealed class EscalationConfiguration : IEntityTypeConfiguration<Escalation>
{
    public void Configure(EntityTypeBuilder<Escalation> builder)
    {
        builder.HasKey(x => x.Id);
        builder.Property(x => x.State).HasConversion<string>().HasMaxLength(20).IsRequired();

        builder.HasOne(x => x.Alert)
            .WithMany()
            .HasForeignKey(x => x.AlertId)
            .OnDelete(DeleteBehavior.Cascade);

        // Restrict, not cascade: a policy with live escalations must not be deletable out from
        // under the engine.
        builder.HasOne(x => x.Policy)
            .WithMany()
            .HasForeignKey(x => x.PolicyId)
            .OnDelete(DeleteBehavior.Restrict);

        // Due-scan index for the engine poll. Only live escalations are indexed, so the
        // once-a-second claim query never scans resolved/exhausted rows (see ADR-001).
        builder.HasIndex(x => x.NextTimeoutAt)
            .HasDatabaseName("IX_Escalations_Due")
            .HasFilter("""("State" IN ('Triggered', 'Notified'))""");

        // At most one open escalation per alert — the invariant that makes flap-suppression real
        // (see ADR-004). Terminal states drop out of the filter, so the fresh alert a key opens
        // after its last incident resolved brings a fresh escalation with it.
        builder.HasIndex(x => x.AlertId)
            .HasDatabaseName("IX_Escalations_Open")
            .IsUnique()
            .HasFilter("""("State" NOT IN ('Resolved', 'Exhausted'))""");

        // The FK's plain index, which the convention would normally add for us: it skips that when an
        // index on the same column already exists, and it compares columns without looking at the
        // filter. IX_Escalations_Open is partial, so an unfiltered lookup by AlertId — what ingestion
        // does on every deduplicated firing — could not use it and scanned the table. The named
        // overload is what declares a second index over the same column rather than reconfiguring
        // the first.
        builder.HasIndex(x => x.AlertId, "IX_Escalations_AlertId");
    }
}

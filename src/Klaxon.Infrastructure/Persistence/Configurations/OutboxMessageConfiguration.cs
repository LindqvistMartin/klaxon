using Klaxon.Core.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Klaxon.Infrastructure.Persistence.Configurations;

internal sealed class OutboxMessageConfiguration : IEntityTypeConfiguration<OutboxMessage>
{
    public void Configure(EntityTypeBuilder<OutboxMessage> builder)
    {
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Type).HasMaxLength(100).IsRequired();
        builder.Property(x => x.Payload).HasColumnType("jsonb").IsRequired();

        // Claim index for the dispatcher poll, mirroring IX_Escalations_Due: delivered rows fall
        // out of the filter, so the scan touches only the backlog however much history accrues.
        builder.HasIndex(x => x.CreatedAt)
            .HasDatabaseName("IX_OutboxMessages_Unprocessed")
            .HasFilter("""("ProcessedAt" IS NULL)""");
    }
}

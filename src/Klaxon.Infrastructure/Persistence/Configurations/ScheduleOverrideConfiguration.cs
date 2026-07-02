using Klaxon.Core.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Klaxon.Infrastructure.Persistence.Configurations;

internal sealed class ScheduleOverrideConfiguration : IEntityTypeConfiguration<ScheduleOverride>
{
    public void Configure(EntityTypeBuilder<ScheduleOverride> builder)
    {
        builder.HasKey(x => x.Id);
        // Resolving "who is on call at T" scans a schedule's overrides by time window.
        builder.HasIndex(x => new { x.ScheduleId, x.StartsAt });
    }
}

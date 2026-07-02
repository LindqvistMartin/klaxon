using Klaxon.Core.Entities;
using Microsoft.EntityFrameworkCore;

namespace Klaxon.Infrastructure.Persistence;

public sealed class KlaxonDbContext(DbContextOptions<KlaxonDbContext> options) : DbContext(options)
{
    public DbSet<Organization> Organizations => Set<Organization>();
    public DbSet<Team> Teams => Set<Team>();
    public DbSet<User> Users => Set<User>();
    public DbSet<Schedule> Schedules => Set<Schedule>();
    public DbSet<ScheduleOverride> ScheduleOverrides => Set<ScheduleOverride>();
    public DbSet<EscalationPolicy> EscalationPolicies => Set<EscalationPolicy>();
    public DbSet<EscalationLevel> EscalationLevels => Set<EscalationLevel>();
    public DbSet<Alert> Alerts => Set<Alert>();
    public DbSet<Escalation> Escalations => Set<Escalation>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
        => modelBuilder.ApplyConfigurationsFromAssembly(typeof(KlaxonDbContext).Assembly);
}

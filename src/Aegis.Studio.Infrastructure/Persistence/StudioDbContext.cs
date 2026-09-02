using Aegis.Studio.Application.Abstractions;
using Aegis.Studio.Domain.Bookings;
using Aegis.Studio.Domain.Communications;
using Microsoft.EntityFrameworkCore;

namespace Aegis.Studio.Infrastructure.Persistence;

public sealed class StudioDbContext : DbContext, IUnitOfWork
{
    public StudioDbContext(DbContextOptions<StudioDbContext> options) : base(options)
    {
    }

    public DbSet<Booking> Bookings => Set<Booking>();
    public DbSet<ServiceOffering> ServiceOfferings => Set<ServiceOffering>();
    public DbSet<Photographer> Photographers => Set<Photographer>();
    public DbSet<AvailabilityRule> AvailabilityRules => Set<AvailabilityRule>();
    public DbSet<Workspace> Workspaces => Set<Workspace>();
    public DbSet<Conversation> Conversations => Set<Conversation>();
    public DbSet<CommunicationMessage> CommunicationMessages => Set<CommunicationMessage>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Booking>().HasKey(item => item.Id);
        modelBuilder.Entity<ServiceOffering>().HasKey(item => item.Id);
        modelBuilder.Entity<Photographer>().HasKey(item => item.Id);
        modelBuilder.Entity<AvailabilityRule>().HasKey(item => item.Id);
        modelBuilder.Entity<Workspace>().HasKey(item => item.Id);
        modelBuilder.Entity<Workspace>().HasIndex(item => item.PublicEmailAddress).IsUnique();
        modelBuilder.Entity<Conversation>().HasKey(item => item.Id);
        modelBuilder.Entity<CommunicationMessage>().HasKey(item => item.Id);
        modelBuilder.Entity<CommunicationMessage>().Property(item => item.Body).HasColumnType("text");
    }

    public override async Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
    {
        return await base.SaveChangesAsync(cancellationToken);
    }

    async Task IUnitOfWork.SaveChangesAsync(CancellationToken cancellationToken)
    {
        await SaveChangesAsync(cancellationToken);
    }
}

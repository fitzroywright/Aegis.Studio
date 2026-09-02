using Aegis.Studio.Application.Abstractions;
using Aegis.Studio.Domain.Bookings;
using Aegis.Studio.Domain.Communications;
using Aegis.Studio.Domain.Operations;
using Aegis.Studio.Domain.Workspaces;
using Microsoft.EntityFrameworkCore;

namespace Aegis.Studio.Infrastructure.Persistence;

public sealed class StudioDbContext : DbContext, IUnitOfWork
{
    public StudioDbContext(DbContextOptions<StudioDbContext> options) : base(options) { }

    public DbSet<Booking> Bookings => Set<Booking>();
    public DbSet<ServiceOffering> ServiceOfferings => Set<ServiceOffering>();
    public DbSet<Photographer> Photographers => Set<Photographer>();
    public DbSet<AvailabilityRule> AvailabilityRules => Set<AvailabilityRule>();
    public DbSet<Workspace> Workspaces => Set<Workspace>();
    public DbSet<Conversation> Conversations => Set<Conversation>();
    public DbSet<CommunicationMessage> CommunicationMessages => Set<CommunicationMessage>();
    public DbSet<WorkspaceMember> WorkspaceMembers => Set<WorkspaceMember>();
    public DbSet<OperationalAlert> OperationalAlerts => Set<OperationalAlert>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Booking>().HasKey(item => item.Id);
        modelBuilder.Entity<Booking>().HasIndex(item => item.Reference).IsUnique();
        modelBuilder.Entity<ServiceOffering>().HasKey(item => item.Id);
        modelBuilder.Entity<Photographer>().HasKey(item => item.Id);
        modelBuilder.Entity<AvailabilityRule>().HasKey(item => item.Id);
        modelBuilder.Entity<Workspace>().HasKey(item => item.Id);
        modelBuilder.Entity<Workspace>().HasIndex(item => item.PublicEmailAddress).IsUnique();
        modelBuilder.Entity<Conversation>().HasKey(item => item.Id);
        modelBuilder.Entity<Conversation>().HasIndex(item => new { item.WorkspaceId, item.LastActivityUtc });
        modelBuilder.Entity<CommunicationMessage>().HasKey(item => item.Id);
        modelBuilder.Entity<CommunicationMessage>().HasIndex(item => new { item.ConversationId, item.CreatedUtc });
        modelBuilder.Entity<CommunicationMessage>().Property(item => item.Body).HasColumnType("text");
        modelBuilder.Entity<WorkspaceMember>().HasKey(item => item.Id);
        modelBuilder.Entity<WorkspaceMember>().HasIndex(item => new { item.WorkspaceId, item.UserId }).IsUnique();
        modelBuilder.Entity<OperationalAlert>().HasKey(item => item.Id);
        modelBuilder.Entity<OperationalAlert>().HasIndex(item => new { item.WorkspaceId, item.IsAcknowledged });
    }

    public override async Task<int> SaveChangesAsync(CancellationToken cancellationToken = default) => await base.SaveChangesAsync(cancellationToken);
    async Task IUnitOfWork.SaveChangesAsync(CancellationToken cancellationToken) => await SaveChangesAsync(cancellationToken);
}

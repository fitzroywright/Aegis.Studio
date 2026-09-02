using Aegis.Studio.Application.Abstractions;
using Aegis.Studio.Domain.Bookings;
using Aegis.Studio.Domain.Communications;
using Aegis.Studio.Domain.Customers;
using Aegis.Studio.Domain.Galleries;
using Aegis.Studio.Domain.Notifications;
using Aegis.Studio.Domain.Operations;
using Aegis.Studio.Domain.Tasks;
using Aegis.Studio.Domain.Workspaces;
using Microsoft.EntityFrameworkCore;

namespace Aegis.Studio.Infrastructure.Persistence;

public sealed class StudioDbContext : DbContext, IUnitOfWork
{
    public StudioDbContext(DbContextOptions<StudioDbContext> options) : base(options) { }
    public DbSet<Booking> Bookings => Set<Booking>(); public DbSet<ServiceOffering> ServiceOfferings => Set<ServiceOffering>(); public DbSet<Photographer> Photographers => Set<Photographer>(); public DbSet<AvailabilityRule> AvailabilityRules => Set<AvailabilityRule>(); public DbSet<Workspace> Workspaces => Set<Workspace>(); public DbSet<Conversation> Conversations => Set<Conversation>(); public DbSet<CommunicationMessage> CommunicationMessages => Set<CommunicationMessage>(); public DbSet<WorkspaceMember> WorkspaceMembers => Set<WorkspaceMember>(); public DbSet<OperationalAlert> OperationalAlerts => Set<OperationalAlert>(); public DbSet<Customer> Customers => Set<Customer>(); public DbSet<StudioTask> StudioTasks => Set<StudioTask>(); public DbSet<NotificationJob> NotificationJobs => Set<NotificationJob>(); public DbSet<Gallery> Galleries => Set<Gallery>(); public DbSet<MediaAsset> MediaAssets => Set<MediaAsset>(); public DbSet<MediaActivity> MediaActivities => Set<MediaActivity>(); public DbSet<ProductionAssignment> ProductionAssignments => Set<ProductionAssignment>(); public DbSet<PrintJob> PrintJobs => Set<PrintJob>();
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Booking>().HasKey(x=>x.Id); modelBuilder.Entity<Booking>().HasIndex(x=>x.Reference).IsUnique(); modelBuilder.Entity<Booking>().HasIndex(x=>x.AccessToken).IsUnique(); modelBuilder.Entity<ServiceOffering>().HasKey(x=>x.Id); modelBuilder.Entity<Photographer>().HasKey(x=>x.Id); modelBuilder.Entity<AvailabilityRule>().HasKey(x=>x.Id); modelBuilder.Entity<Workspace>().HasKey(x=>x.Id); modelBuilder.Entity<Workspace>().HasIndex(x=>x.PublicEmailAddress).IsUnique(); modelBuilder.Entity<Conversation>().HasKey(x=>x.Id); modelBuilder.Entity<Conversation>().HasIndex(x=>new{x.WorkspaceId,x.LastActivityUtc}); modelBuilder.Entity<CommunicationMessage>().HasKey(x=>x.Id); modelBuilder.Entity<CommunicationMessage>().HasIndex(x=>new{x.ConversationId,x.CreatedUtc}); modelBuilder.Entity<CommunicationMessage>().Property(x=>x.Body).HasColumnType("text"); modelBuilder.Entity<WorkspaceMember>().HasKey(x=>x.Id); modelBuilder.Entity<WorkspaceMember>().HasIndex(x=>new{x.WorkspaceId,x.UserId}).IsUnique(); modelBuilder.Entity<OperationalAlert>().HasKey(x=>x.Id); modelBuilder.Entity<Customer>().HasKey(x=>x.Id); modelBuilder.Entity<Customer>().HasIndex(x=>x.Email).IsUnique(); modelBuilder.Entity<StudioTask>().HasKey(x=>x.Id); modelBuilder.Entity<NotificationJob>().HasKey(x=>x.Id); modelBuilder.Entity<NotificationJob>().Property(x=>x.Body).HasColumnType("text");
        modelBuilder.Entity<Gallery>().HasKey(x=>x.Id); modelBuilder.Entity<Gallery>().HasIndex(x=>new{x.WorkspaceId,x.BookingId}); modelBuilder.Entity<MediaAsset>().HasKey(x=>x.Id); modelBuilder.Entity<MediaAsset>().HasIndex(x=>new{x.GalleryId,x.UpdatedUtc}); modelBuilder.Entity<MediaActivity>().HasKey(x=>x.Id); modelBuilder.Entity<MediaActivity>().HasIndex(x=>new{x.MediaAssetId,x.CreatedUtc}); modelBuilder.Entity<ProductionAssignment>().HasKey(x=>x.Id); modelBuilder.Entity<ProductionAssignment>().HasIndex(x=>new{x.WorkspaceId,x.Status}); modelBuilder.Entity<PrintJob>().HasKey(x=>x.Id); modelBuilder.Entity<PrintJob>().HasIndex(x=>new{x.WorkspaceId,x.Status,x.ScheduledUtc});
    }
    public override async Task<int> SaveChangesAsync(CancellationToken cancellationToken=default)=>await base.SaveChangesAsync(cancellationToken); async Task IUnitOfWork.SaveChangesAsync(CancellationToken cancellationToken)=>await SaveChangesAsync(cancellationToken);
}

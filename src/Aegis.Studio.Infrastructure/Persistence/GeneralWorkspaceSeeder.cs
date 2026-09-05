using Aegis.Studio.Domain.Communications;
using Microsoft.EntityFrameworkCore;

namespace Aegis.Studio.Infrastructure.Persistence;

public sealed class GeneralWorkspaceSeeder
{
    public const string Slug = "general";
    public const string Name = "General";
    public const string PublicEmailAddress = "general@bryans.com";

    private readonly StudioDbContext dbContext;

    public GeneralWorkspaceSeeder(StudioDbContext dbContext)
    {
        this.dbContext = dbContext;
    }

    public async Task SeedAsync(CancellationToken cancellationToken)
    {
        bool exists = await dbContext.Workspaces.AnyAsync(item => item.Slug == Slug, cancellationToken);
        if (exists)
        {
            return;
        }

        Workspace workspace = new(Name, Slug, PublicEmailAddress);
        dbContext.Workspaces.Add(workspace);
        await dbContext.SaveChangesAsync(cancellationToken);
    }
}

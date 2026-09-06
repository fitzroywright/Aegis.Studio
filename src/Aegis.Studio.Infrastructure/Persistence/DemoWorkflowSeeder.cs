using System.Text;
using Aegis.Studio.Application.Abstractions;
using Aegis.Studio.Domain.Bookings;
using Aegis.Studio.Domain.Communications;
using Aegis.Studio.Domain.Customers;
using Aegis.Studio.Domain.Galleries;
using Microsoft.EntityFrameworkCore;

namespace Aegis.Studio.Infrastructure.Persistence;

public sealed class DemoWorkflowSeeder
{
    public const string ScenarioName = "DEMO - Morgan Wedding - Full Workflow";

    private readonly StudioDbContext dbContext;
    private readonly IMediaStorage mediaStorage;

    public DemoWorkflowSeeder(StudioDbContext dbContext, IMediaStorage mediaStorage)
    {
        this.dbContext = dbContext;
        this.mediaStorage = mediaStorage;
    }

    public async Task SeedAsync(CancellationToken cancellationToken)
    {
        bool alreadySeeded = await dbContext.Galleries.AnyAsync(x => x.Name == $"{ScenarioName} - Photographer Uploads", cancellationToken);
        if (alreadySeeded)
        {
            return;
        }

        Workspace weddings = await dbContext.Workspaces.SingleAsync(x => x.Slug == "weddings", cancellationToken);
        ServiceOffering service = await dbContext.ServiceOfferings.SingleAsync(x => x.Name == "Wedding Photography", cancellationToken);
        Photographer photographer = await dbContext.Photographers.OrderBy(x => x.Name).FirstAsync(cancellationToken);

        Customer customer = await EnsureCustomerAsync(cancellationToken);
        Booking booking = await EnsureBookingAsync(weddings, service, photographer, cancellationToken);
        Conversation conversation = await EnsureConversationAsync(weddings, booking, cancellationToken);
        await SeedConversationAsync(conversation, weddings, cancellationToken);

        Gallery photographerGallery = new($"{ScenarioName} - Photographer Uploads", GalleryKind.Job, weddings.Id, booking.Id, customer.Id);
        Gallery productionGallery = new($"{ScenarioName} - Production Edits", GalleryKind.Production, weddings.Id, booking.Id, customer.Id);
        Gallery customerGallery = new($"{ScenarioName} - Customer Proofs", GalleryKind.Customer, weddings.Id, booking.Id, customer.Id);
        dbContext.Galleries.AddRange(photographerGallery, productionGallery, customerGallery);
        await dbContext.SaveChangesAsync(cancellationToken);

        MediaAsset originalWide = await AddImageAsync(photographerGallery, "IMG_4102_original.svg", "Ryan Bryan - Photographer", "Photographer upload", "Original camera file · ceremony wide", "ORIGINAL", cancellationToken);
        MediaAsset originalPortrait = await AddImageAsync(photographerGallery, "IMG_4188_original.svg", "Ryan Bryan - Photographer", "Photographer upload", "Original camera file · couple portrait", "ORIGINAL", cancellationToken);
        MediaAsset originalFamily = await AddImageAsync(photographerGallery, "IMG_4231_original.svg", "Ryan Bryan - Photographer", "Photographer upload", "Original camera file · family group", "ORIGINAL", cancellationToken);

        AddActivity(originalWide, "Ryan Bryan - Photographer", "Uploaded from shoot", "Camera card ingest complete; checksum verified.");
        AddActivity(originalPortrait, "Ryan Bryan - Photographer", "Uploaded from shoot", "Flagged as photographer select for retouching.");
        AddActivity(originalFamily, "Ryan Bryan - Photographer", "Uploaded from shoot", "Family group selected for proof set.");

        ProductionAssignment portraitAssignment = new(originalPortrait.Id, weddings.Id, "Keisha - Production", "Color correction, skin retouch, crop and print preparation");
        dbContext.ProductionAssignments.Add(portraitAssignment);
        AddActivity(originalPortrait, "Production Manager", "Production assigned", "Retouching assigned to Keisha - Production.");
        portraitAssignment.Complete();
        AddActivity(originalPortrait, "Keisha - Production", "Production completed", "Color corrected, retouched and master crop approved.");

        ProductionAssignment familyAssignment = new(originalFamily.Id, weddings.Id, "Andre - Production", "Exposure balance, background cleanup and crop");
        dbContext.ProductionAssignments.Add(familyAssignment);
        AddActivity(originalFamily, "Production Manager", "Production assigned", "Family group cleanup assigned to Andre - Production.");
        familyAssignment.Complete();
        AddActivity(originalFamily, "Andre - Production", "Production completed", "Background distractions removed and final crop prepared.");

        MediaAsset editedPortrait = await AddImageAsync(productionGallery, "IMG_4188_retouched_v2.svg", "Keisha - Production", "Production edit", "Retouched master · version 2", "RETOUCHED", cancellationToken);
        MediaAsset editedFamily = await AddImageAsync(productionGallery, "IMG_4231_color_corrected.svg", "Andre - Production", "Production edit", "Color corrected family master", "EDITED", cancellationToken);
        AddActivity(editedPortrait, "Keisha - Production", "Derived from original", $"Created from {originalPortrait.FileName}; final retouch master.");
        AddActivity(editedPortrait, "Michelle - QA", "Quality approved", "Skin tone, crop, exposure and print sharpness approved.");
        AddActivity(editedFamily, "Andre - Production", "Derived from original", $"Created from {originalFamily.FileName}; cleanup master.");
        AddActivity(editedFamily, "Michelle - QA", "Quality approved", "Group composition and color approved for proofing.");

        MediaAsset customerProofOne = await AddImageAsync(customerGallery, "PROOF_01_couple.svg", "Bryan Studios", "Customer proof", "Watermarked proof · couple portrait", "PROOF", cancellationToken);
        MediaAsset customerProofTwo = await AddImageAsync(customerGallery, "PROOF_02_family.svg", "Bryan Studios", "Customer proof", "Watermarked proof · family group", "PROOF", cancellationToken);
        AddActivity(customerProofOne, "Bryan Studios", "Published to customer", "Proof gallery notification sent to danielle.morgan@example.com.");
        AddActivity(customerProofOne, "Danielle Morgan", "Customer viewed", "Customer opened proof gallery from email link.");
        AddActivity(customerProofOne, "Danielle Morgan", "Customer approved", "Approved for 8x10 print and final digital delivery.");
        AddActivity(customerProofTwo, "Danielle Morgan", "Customer viewed", "Customer opened family proof.");
        AddActivity(customerProofTwo, "Danielle Morgan", "Change requested", "Please crop slightly tighter on the left side before final print.");
        AddActivity(customerProofTwo, "Andre - Production", "Revision completed", "Customer crop request applied; revised proof ready.");
        AddActivity(customerProofTwo, "Danielle Morgan", "Customer approved", "Revised family proof approved.");

        PrintJob portraitPrint = new(customerProofOne.Id, weddings.Id, "Danielle Morgan", 2, "8x10 Lustre · retouched couple portrait", DateTimeOffset.UtcNow.AddDays(1));
        PrintJob familyPrint = new(customerProofTwo.Id, weddings.Id, "Danielle Morgan", 1, "11x14 Lustre · family group", DateTimeOffset.UtcNow.AddDays(1));
        dbContext.PrintJobs.AddRange(portraitPrint, familyPrint);
        AddActivity(customerProofOne, "Front Desk", "Print scheduled", "2 x 8x10 Lustre added to production queue.");
        AddActivity(customerProofTwo, "Front Desk", "Print scheduled", "1 x 11x14 Lustre added to production queue.");

        await dbContext.SaveChangesAsync(cancellationToken);
    }

    private async Task<Customer> EnsureCustomerAsync(CancellationToken cancellationToken)
    {
        Customer? existing = await dbContext.Customers.SingleOrDefaultAsync(x => x.Email == "danielle.morgan@example.com", cancellationToken);
        if (existing is not null)
        {
            return existing;
        }

        Customer customer = new("Danielle Morgan", "danielle.morgan@example.com", "876-555-0101");
        dbContext.Customers.Add(customer);
        await dbContext.SaveChangesAsync(cancellationToken);
        return customer;
    }

    private async Task<Booking> EnsureBookingAsync(Workspace workspace, ServiceOffering service, Photographer photographer, CancellationToken cancellationToken)
    {
        Booking? existing = await dbContext.Bookings
            .Where(x => x.WorkspaceId == workspace.Id && x.ClientEmail == "danielle.morgan@example.com")
            .OrderByDescending(x => x.CreatedUtc)
            .FirstOrDefaultAsync(cancellationToken);

        if (existing is not null)
        {
            return existing;
        }

        DateTimeOffset start = DateTimeOffset.UtcNow.AddDays(21).Date.AddHours(15);
        Booking booking = new(workspace.Id, service.Id, photographer.Id, "Danielle Morgan", "danielle.morgan@example.com", "876-555-0101", start, start.AddHours(8));
        dbContext.Bookings.Add(booking);
        await dbContext.SaveChangesAsync(cancellationToken);
        return booking;
    }

    private async Task<Conversation> EnsureConversationAsync(Workspace workspace, Booking booking, CancellationToken cancellationToken)
    {
        Conversation? existing = await dbContext.Conversations.SingleOrDefaultAsync(x => x.WorkspaceId == workspace.Id && x.Subject == "DEMO FLOW - Morgan wedding production", cancellationToken);
        if (existing is not null)
        {
            return existing;
        }

        Conversation conversation = new(workspace.Id, booking.Id, booking.ClientEmail, "DEMO FLOW - Morgan wedding production");
        dbContext.Conversations.Add(conversation);
        await dbContext.SaveChangesAsync(cancellationToken);
        return conversation;
    }

    private async Task SeedConversationAsync(Conversation conversation, Workspace workspace, CancellationToken cancellationToken)
    {
        bool hasMessages = await dbContext.CommunicationMessages.AnyAsync(x => x.ConversationId == conversation.Id, cancellationToken);
        if (hasMessages)
        {
            return;
        }

        AddMessage(conversation, MessageDirection.Inbound, "danielle.morgan@example.com", workspace.PublicEmailAddress, "Wedding photography and availability", "Hi Bryan Studios, we are getting married next month and would like full-day coverage. Can you confirm availability and the deposit required?");
        AddMessage(conversation, MessageDirection.Outbound, workspace.PublicEmailAddress, "danielle.morgan@example.com", "Re: Wedding photography and availability", "Hi Danielle, yes, we have availability. We have tentatively held the date and attached the package details. A deposit will confirm the booking.");
        AddMessage(conversation, MessageDirection.Inbound, "danielle.morgan@example.com", workspace.PublicEmailAddress, "Re: Wedding photography and availability", "Perfect. Please confirm the booking. We would also like family portraits immediately after the ceremony.");
        AddMessage(conversation, MessageDirection.Internal, "Front Desk - Marsha", "internal", "Internal note", "Customer confirmed by phone. Deposit received at front desk. Photographer Ryan assigned. Family list requested from customer.");
        AddMessage(conversation, MessageDirection.Outbound, workspace.PublicEmailAddress, "danielle.morgan@example.com", "Booking confirmed", "Your booking is confirmed. Ryan will photograph the wedding. Please send the family group list before the event.");
        AddMessage(conversation, MessageDirection.Internal, "Ryan Bryan - Photographer", "internal", "Shoot completed", "Wedding photographed successfully. 684 images captured. Photographer selects uploaded to the job gallery and production can begin.");
        AddMessage(conversation, MessageDirection.Outbound, workspace.PublicEmailAddress, "danielle.morgan@example.com", "Your wedding proofs are ready", "Hi Danielle, your first proof selections are ready. Please review the couple portrait and family group and send any changes through the proof workflow.");
        AddMessage(conversation, MessageDirection.Inbound, "danielle.morgan@example.com", workspace.PublicEmailAddress, "Re: Your wedding proofs are ready", "The couple portrait is perfect. For the family photo, can you crop a little tighter on the left? After that we can approve it for print.");
        AddMessage(conversation, MessageDirection.Outbound, workspace.PublicEmailAddress, "danielle.morgan@example.com", "Revised family proof ready", "Done. Production applied the crop adjustment and the revised proof is ready. Both approved images are now queued for printing.");

        await dbContext.SaveChangesAsync(cancellationToken);
    }

    private void AddMessage(Conversation conversation, MessageDirection direction, string fromAddress, string toAddress, string subject, string body)
    {
        CommunicationMessage message = new(conversation.Id, direction, fromAddress, toAddress, subject, body, direction == MessageDirection.Inbound ? $"demo-flow-{Guid.NewGuid():N}@example.com" : null);
        dbContext.CommunicationMessages.Add(message);
        if (direction == MessageDirection.Inbound)
        {
            conversation.ReceiveInbound();
        }
        else
        {
            conversation.Touch();
        }
    }

    private async Task<MediaAsset> AddImageAsync(Gallery gallery, string fileName, string uploadedBy, string action, string detail, string stage, CancellationToken cancellationToken)
    {
        string svg = BuildSampleImage(stage, fileName, detail);
        byte[] bytes = Encoding.UTF8.GetBytes(svg);
        await using MemoryStream stream = new(bytes);
        string storageKey = await mediaStorage.SaveAsync(fileName, stream, cancellationToken);
        MediaAsset asset = new(gallery.Id, fileName, "image/svg+xml", bytes.Length, storageKey, uploadedBy);
        dbContext.MediaAssets.Add(asset);
        dbContext.MediaActivities.Add(new MediaActivity(asset.Id, uploadedBy, action, detail));
        await dbContext.SaveChangesAsync(cancellationToken);
        return asset;
    }

    private void AddActivity(MediaAsset asset, string userId, string action, string detail)
    {
        dbContext.MediaActivities.Add(new MediaActivity(asset.Id, userId, action, detail));
    }

    private static string BuildSampleImage(string stage, string fileName, string detail)
    {
        string safeFileName = System.Net.WebUtility.HtmlEncode(fileName);
        string safeDetail = System.Net.WebUtility.HtmlEncode(detail);
        string safeStage = System.Net.WebUtility.HtmlEncode(stage);
        return $"""
            <svg xmlns="http://www.w3.org/2000/svg" width="1200" height="800" viewBox="0 0 1200 800">
              <defs>
                <linearGradient id="bg" x1="0" y1="0" x2="1" y2="1"><stop offset="0" stop-color="#211b17"/><stop offset="1" stop-color="#806b56"/></linearGradient>
              </defs>
              <rect width="1200" height="800" fill="url(#bg)"/>
              <circle cx="930" cy="250" r="185" fill="#efe4d3" opacity=".18"/>
              <rect x="120" y="135" width="570" height="430" rx="8" fill="#f7f1e8" opacity=".93"/>
              <rect x="160" y="175" width="490" height="290" fill="#b8aa99"/>
              <circle cx="405" cy="300" r="95" fill="#6f6154"/>
              <path d="M270 455 C315 355 500 355 545 455" fill="#6f6154"/>
              <text x="120" y="650" fill="#f7f1e8" font-family="Georgia,serif" font-size="58">Bryan Studios</text>
              <text x="120" y="705" fill="#d7c4a6" font-family="Arial,sans-serif" font-size="24" letter-spacing="5">{safeStage}</text>
              <text x="735" y="530" fill="#ffffff" font-family="Arial,sans-serif" font-size="26">{safeFileName}</text>
              <text x="735" y="575" fill="#e8ded1" font-family="Arial,sans-serif" font-size="20">{safeDetail}</text>
              <text x="735" y="620" fill="#d7c4a6" font-family="Arial,sans-serif" font-size="17">SAMPLE WORK PRODUCT · NOT A CUSTOMER PHOTO</text>
            </svg>
            """;
    }
}

using System.Text.Json;
namespace Aegis.Studio.Web.Crm;

public sealed record CrmClientRequest(string DisplayName,string? Email=null,string? Phone=null,string ClientType="Individual",string? LegalName=null,string? FirstName=null,string? LastName=null,string? OrganisationName=null,Guid? PrimaryWorkspaceId=null,string? AccountOwner=null,string RelationshipStatus="Active",DateOnly? ClientSince=null,string? Source=null,string[]? Tags=null,JsonElement? Metadata=null);
public sealed record CrmTaskRequest(Guid WorkspaceId,string AssignedTo,string TaskType,DateTimeOffset DueAtUtc,string Priority="Normal",string Status="Open",string? Notes=null,Guid? JobId=null,Guid? EnquiryId=null);
public sealed record CrmContactRequest(Guid PersonId,string Role,bool IsPrimaryBilling=false,bool IsBookingContact=false);
public sealed record CrmNoteRequest(string Body,string Kind="Internal note");
public sealed record CrmViewRequest(string Name,JsonElement Definition);
public sealed record CrmBulkRequest(Guid[] Ids,string[] Tags,string? RelationshipStatus=null);
public sealed record CrmMergeRequest(Guid SourceId,string Reason,string PreviewToken);
public sealed record CrmLeadRequest(string Status,JsonElement Metadata);
public sealed record CrmQuery(string? Search=null,string? Type=null,string? Status=null,Guid? Workspace=null,string? Owner=null,string? FollowUp=null,string? Sort=null,int Page=1,int PageSize=50,int? InactiveDays=null);
public static class CrmRules
{
    public static readonly string[] Types=["Individual","Family / Household","School","Corporate","Organisation"];
    public static readonly string[] Statuses=["Prospect","Active","Inactive","Archived"];
    public static readonly string[] LeadStatuses=["New","Contacted","Qualified","Quoted","Awaiting Decision","Won","Lost"];
    public static readonly string[] TaskTypes=["Call Client","Send Email","Follow Up on Quote","Confirm Booking","Request Documents","Request Payment","Confirm Delivery","General Follow-up"];
    public static string NormalizeEmail(string? value)=>value?.Trim().ToLowerInvariant()??"";
    public static string NormalizePhone(string? value) { string digits=new((value??"").Where(char.IsAsciiDigit).ToArray());return digits.Length==10&&(digits.StartsWith("876",StringComparison.Ordinal)||digits.StartsWith("658",StringComparison.Ordinal))?"1"+digits:digits; }
    public static void Validate(CrmClientRequest r)
    {
        if(string.IsNullOrWhiteSpace(r.DisplayName)||r.DisplayName.Length>200||!Types.Contains(r.ClientType)||!Statuses.Contains(r.RelationshipStatus))throw new ArgumentException("Enter a name, supported client type and relationship status.");
        if((r.Email?.Length??0)>320||(!string.IsNullOrWhiteSpace(r.Email)&&!System.Net.Mail.MailAddress.TryCreate(r.Email,out _))||(r.Phone?.Length??0)>80)throw new ArgumentException("Enter valid contact details.");
        if(new[]{r.LegalName,r.FirstName,r.LastName,r.OrganisationName,r.AccountOwner,r.Source}.Any(s=>(s?.Length??0)>200)||(r.Tags?.Length??0)>40||r.Tags?.Any(t=>string.IsNullOrWhiteSpace(t)||t.Length>60)==true)throw new ArgumentException("Client fields or tags are too long.");
        if(r.Metadata is {} m&&(m.ValueKind!=JsonValueKind.Object||m.GetRawText().Length>30000))throw new ArgumentException("Metadata must be an object of at most 30000 characters.");
        if(r.Source is not null)Aegis.Studio.Domain.Bookings.BookingSources.RequireSelectable(r.Source);
        if(r.Metadata is {} metadata)
        {
            foreach(string key in new[]{"additionalContacts","consentRecords"})if(metadata.TryGetProperty(key,out var field)&&field.ValueKind!=JsonValueKind.Array)throw new ArgumentException(key+" must be a JSON array.");
            foreach(string key in new[]{"importantDates","customFields","communicationPreferences"})if(metadata.TryGetProperty(key,out var field)&&field.ValueKind!=JsonValueKind.Object)throw new ArgumentException(key+" must be a JSON object.");
            if(metadata.TryGetProperty("communicationPreferences",out var preferences))foreach(string key in new[]{"optOut","emailAllowed","smsAllowed","whatsAppAllowed","whatsappAllowed"})if(preferences.TryGetProperty(key,out var flag)&&flag.ValueKind is not (JsonValueKind.True or JsonValueKind.False))throw new ArgumentException("Communication preferences must use true or false.");
            if(metadata.TryGetProperty("consentRecords",out var consents))foreach(var consent in consents.EnumerateArray())if(consent.ValueKind!=JsonValueKind.Object||!consent.TryGetProperty("channel",out var channel)||channel.ValueKind!=JsonValueKind.String||!new[]{"Email","SMS","WhatsApp","Telephone"}.Contains(channel.GetString())||!consent.TryGetProperty("granted",out var granted)||granted.ValueKind is not (JsonValueKind.True or JsonValueKind.False))throw new ArgumentException("Each consent record needs a supported channel and a boolean granted value.");
        }

    }
    public static void Validate(CrmTaskRequest r)
    {
        if(r.WorkspaceId==Guid.Empty||string.IsNullOrWhiteSpace(r.AssignedTo)||r.AssignedTo.Length>200||!TaskTypes.Contains(r.TaskType)||!new[]{"Open","In Progress","Completed","Cancelled"}.Contains(r.Status)||!new[]{"Low","Normal","High","Urgent"}.Contains(r.Priority)||r.DueAtUtc==default||(r.Notes?.Length??0)>4000)throw new ArgumentException("Supply a workspace, staff owner, due date, supported task type, priority and status.");
    }
    public static string OrderBy(string? value,bool financials)
    {
        var columns=new Dictionary<string,string>{{"name","lower(c.display_name)"},{"type","c.client_type"},{"email","c.email"},{"phone","c.phone"},{"workspace","w.name"},{"jobs","coalesce(j.total_jobs,0)"},{"lastJob","j.last_job"},{"lastContact","contact.last_contact"},{"followUp","t.next_follow_up"},{"status","c.relationship_status"}};
        if(financials)columns["value"]="coalesce(f.lifetime_value,0)";
        var parts=new List<string>();foreach(var item in (value??"name").Split(',').Take(4)){bool desc=item.StartsWith('-');string key=desc?item[1..]:item;if(!columns.TryGetValue(key,out var column))throw new ArgumentException("Unsupported sort column.");parts.Add(column+(desc?" desc":" asc")+" nulls last");}parts.Add("c.id");return string.Join(",",parts);
    }
}
public sealed record CrmEnquiryRequest(Guid WorkspaceId,string Subject,string Body);

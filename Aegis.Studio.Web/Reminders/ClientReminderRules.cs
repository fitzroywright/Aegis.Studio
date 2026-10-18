using System.Text.RegularExpressions;
namespace Aegis.Studio.Web.Reminders;

public sealed record ReminderTemplate(string Kind,string Label,int OffsetHours,string Subject,string Body);
public static class ClientReminderRules
{
    public static readonly ReminderTemplate[] Templates = [
        new("Shoot","Upcoming shoot",-48,"Your upcoming session: {job}","Hello {client}, your session for {job} is scheduled for {detail} at {location}. Please contact us if you need to make a change."),
        new("Deposit","Outstanding deposit",24,"Deposit reminder: {job}","Hello {client}, the outstanding deposit for {job} is {detail}. Please contact us to arrange payment or provide your payment reference."),
        new("Selection","Client selection",48,"Your selections: {job}","Hello {client}, we are waiting for your response for {job}: {detail}. Please contact us when you are ready."),
        new("Approval","Client approval",48,"Approval reminder: {job}","Hello {client}, your approval is needed for {job}: {detail}. Please contact us with your decision."),
        new("Collection","Ready for collection / delivery",72,"Ready for collection: {job}","Hello {client}, your order for {job} is ready. Please contact us to arrange collection or delivery."),
        new("FollowUp","General follow-up",24,"Following up: {job}","Hello {client}, we are following up about {job}. Please contact us when convenient.")
    ];
    public static void Validate(string kind,string channel,string subject,string body)
    {
        if(!Templates.Any(t=>t.Kind==kind))throw new ArgumentException("Choose a supported reminder type.");
        if(channel is not ("Email" or "WhatsApp"))throw new ArgumentException("Choose Email or WhatsApp.");
        if(string.IsNullOrWhiteSpace(subject)||subject.Length>200||string.IsNullOrWhiteSpace(body)||body.Length>4000)throw new ArgumentException("Enter a subject (up to 200 characters) and message (up to 4000 characters).");
        foreach(Match m in Regex.Matches(subject+body,@"\{([^{}]+)\}"))
            if(m.Groups[1].Value is not ("client" or "job" or "detail" or "location"))throw new ArgumentException("Use only {client}, {job}, {detail} and {location} placeholders.");
    }
    public static string Render(string text,string client,string job,string detail,string location)
        =>Regex.Replace(text,@"\{(client|job|detail|location)\}",m=>m.Groups[1].Value switch {"client"=>client,"job"=>job,"detail"=>detail,_=>location});
    public static bool IsQuiet(DateTimeOffset now,TimeZoneInfo zone)
    {int hour=TimeZoneInfo.ConvertTime(now,zone).Hour;return hour<9||hour>=18;}
}
public sealed record ReminderPreferences(bool EmailAllowed,bool WhatsAppAllowed);
public sealed record ReminderRuleRequest(string Kind,bool Enabled,string Channel,int OffsetHours,string Subject,string Body);
public sealed record ReminderRequest(Guid Id,string Kind,Guid? SourceId,string Channel,DateTimeOffset ScheduledAtUtc,string Subject,string Body);
public sealed record ReminderChange(string Action,DateTimeOffset? ScheduledAtUtc=null,string? Subject=null,string? Body=null);
public sealed record ReminderSource(string Kind,Guid SourceId,DateTimeOffset AnchorAtUtc,string Detail,string Location);

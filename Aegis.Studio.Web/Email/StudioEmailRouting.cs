using System.Net.Mail;
using System.Text.RegularExpressions;
using Aegis.Studio.Application.EmailRouting;
using Aegis.Studio.Web.Persistence;
using Npgsql;
using static Aegis.Studio.Web.Messaging.StudioUnifiedMessaging;

namespace Aegis.Studio.Web.Email;

public sealed record EmailAssociation(Guid? ConversationId, Guid? ThreadId);
public sealed record EmailRoutingDecision(EmailAssociation? Association, EmailRouteResult Workspace,
    string? ReviewReason, EmailAssociation[] Candidates);

// All intake entry points call this before workspace or direct-inbox fallback.
public sealed class StudioEmailRouting(StudioDatabase database, IInboundEmailRouter workspaces)
{
    public static string NormalizeAddress(string? value)
        => MailAddress.TryCreate(value?.Trim(), out var address) ? address.Address.Trim().ToLowerInvariant() : "";
    public static string PublicReference(Guid id, bool thread = false) => "AEGIS-" + (thread ? "T" : "") + id.ToString("N").ToUpperInvariant();
    public static string[] MessageIds(string? value) => Regex.Matches(value ?? "", @"<?[^<>\s,]+@[^<>\s,]+>?")
        .Select(m => "<" + m.Value.Trim('<','>') + ">").Distinct(StringComparer.Ordinal).ToArray();
    public static string Header(InboundEmail email, string name) => email.InternetHeaders.FirstOrDefault(h => h.Key.Equals(name,StringComparison.OrdinalIgnoreCase)).Value ?? "";
    public static string[] ExplicitReferences(string subject) => Regex.Matches(subject, @"\bAEGIS-(?:T[0-9A-F]{32}|[0-9A-F]{32}|[0-9A-F]{8})\b", RegexOptions.IgnoreCase)
        .Select(m=>m.Value.ToUpperInvariant()).Distinct(StringComparer.Ordinal).ToArray();

    public EmailRoutingDecision Resolve(InboundEmail email)
    {
        var workspace = workspaces.Route(email);
        using var c = database.OpenConnection();
        // A completed import is authoritative, including staff corrections and split messages.
        using(var prior = new NpgsqlCommand("select conversation_id,thread_id from studio_email_intake_receipts where external_message_id=$1 or ($2::text is not null and provider_message_id=$2)",c))
        {
            Add(prior,email.MessageId,email.ProviderMessageId); using var r=prior.ExecuteReader();
            List<EmailAssociation> recorded=[];
            while(r.Read())recorded.Add(new(r.IsDBNull(0)?null:r.GetGuid(0),r.IsDBNull(1)?null:r.GetGuid(1)));
            var unique=recorded.Distinct().ToArray();
            if(unique.Length>1)return new(null,workspace,"Provider and Internet Message-ID associations conflict—staff review required",unique);
            if(unique.Length==1)return new(unique[0],workspace,null,[]);
        }
        var immediate = MessageIds(Header(email,"In-Reply-To"));
        var ancestors = MessageIds(Header(email,"References"));
        var immediateMatches = FindReplies(c,immediate);
        var ancestorMatches = FindReplies(c,ancestors);
        var explicitTokens = ExplicitReferences(email.Subject);
        List<EmailAssociation> explicitMatches=[]; bool invalidReference=false;
        foreach(var reference in explicitTokens)
        {
            var matches=FindReference(c,reference);
            if(matches.Length!=1) invalidReference=true;
            explicitMatches.AddRange(matches);
        }
        // An unmatched immediate target can still have recorded ancestors. Multiple known
        // conversations anywhere in the chain are held, rather than selecting an arbitrary row.
        var replyMatches = immediateMatches.Length>0 ? immediateMatches : ancestorMatches;
        var trusted=replyMatches.Concat(ancestorMatches).Concat(explicitMatches).Distinct().ToArray();
        var claimed=FindUnverifiedReplies(c,immediate.Concat(ancestors).Distinct().ToArray());
        var all=trusted.Concat(claimed).Distinct().ToArray();
        if(invalidReference || all.Length>1)
            return new(null,workspace,"Conflicting, invalid or ambiguous email references—staff review required",all);
        if(trusted.Length==0 && claimed.Length>0)
            return new(null,workspace,"Reply identifier awaits provider verification—staff review required",all);
        if(all.Length==1)
        {
            var target=all[0];
            if(target.ThreadId is Guid historicalThread)
            {
                using var scope=new NpgsqlCommand("select scope_type from studio_message_threads where id=$1",c);Add(scope,historicalThread);
                if(scope.ExecuteScalar() is string kind && kind=="Workspace")
                    return new(null,workspace,"Historical shared workspace conversation—select an inquiry or job",all);
            }
            if(!Recognized(c,target,email.FromAddress))
                return new(null,workspace,"Reference matched—unrecognized sender",all);
            return new(target,workspace,null,all);
        }
        // A malformed AEGIS token is visible for review. Never guess from subject or sender.
        if(email.Subject.Contains("AEGIS-",StringComparison.OrdinalIgnoreCase))
            return new(null,workspace,"Invalid email reference—staff review required",[]);
        return new(null,workspace,null,[]);
    }

    internal static EmailAssociation[] FindReplies(NpgsqlConnection c,string[] ids)
    {
        if(ids.Length==0)return [];
        using var q=new NpgsqlCommand("""
            select distinct e.conversation_id,e.studio_thread_id from studio_outbound_email e
            join studio_outbound_message_ids i on i.delivery_id=e.id where i.internet_message_id=any($1)
            union
            select distinct conversation_id,studio_thread_id from studio_outbound_email
            where rfc_message_id=any($1) and rfc_message_id_verified
            """,c);Add(q,(object)ids);using var r=q.ExecuteReader();List<EmailAssociation> found=[];
        while(r.Read())if(!r.IsDBNull(0)||!r.IsDBNull(1))found.Add(new(r.IsDBNull(0)?null:r.GetGuid(0),r.IsDBNull(1)?null:r.GetGuid(1)));
        return found.Distinct().ToArray();
    }
    private static EmailAssociation[] FindUnverifiedReplies(NpgsqlConnection c,string[] ids)
    {
        if(ids.Length==0)return [];
        using var q=new NpgsqlCommand("select distinct conversation_id,studio_thread_id from studio_outbound_email where rfc_message_id=any($1) and not rfc_message_id_verified",c);
        Add(q,(object)ids);using var r=q.ExecuteReader();List<EmailAssociation> values=[];
        while(r.Read())if(!r.IsDBNull(0)||!r.IsDBNull(1))values.Add(new(r.IsDBNull(0)?null:r.GetGuid(0),r.IsDBNull(1)?null:r.GetGuid(1)));
        return values.ToArray();
    }
    private static EmailAssociation[] FindReference(NpgsqlConnection c,string reference)
    {
        using var q=new NpgsqlCommand("""
            select conversation_id,thread_id from studio_email_public_references where reference=$1
            union select id,null::uuid from studio_conversations
            where $2 and upper(left(replace(id::text,'-',''),8))=substring($1 from 7)
            """,c);Add(q,reference,reference.Length==14);using var r=q.ExecuteReader();List<EmailAssociation> values=[];
        while(r.Read())values.Add(new(r.IsDBNull(0)?null:r.GetGuid(0),r.IsDBNull(1)?null:r.GetGuid(1)));
        // New conversations need no separately committed alias row to be routable.
        if(values.Count==0 && (reference.Length==38 || reference.Length==39) && Guid.TryParseExact(reference[(reference.Length==39?7:6)..],"N",out var id))
        {
            r.Close();using var direct=new NpgsqlCommand(reference.Length==39?"select exists(select 1 from studio_message_threads where id=$1)":"select exists(select 1 from studio_conversations where id=$1)",c);Add(direct,id);
            if((bool)direct.ExecuteScalar()!)values.Add(reference.Length==39?new(null,id):new(id,null));
        }
        return values.Distinct().ToArray();
    }
    private static bool Recognized(NpgsqlConnection c,EmailAssociation target,string sender)
    {
        using var q=new NpgsqlCommand("""
            select exists(
                select 1 from studio_conversations v left join studio_jobs j on j.id=v.job_id
                left join studio_clients cl on cl.id=coalesce(j.client_id,v.client_id)
                where v.id=$1 and (studio_normalize_email(v.contact_address)=$3 or studio_normalize_email(cl.email)=$3)
                union all select 1 from studio_email_contacts ac join studio_conversations acv on acv.id=ac.conversation_id
                join studio_conversations target on target.id=$1
                where (ac.conversation_id=$1 or (target.job_id is not null and acv.job_id=target.job_id)) and ac.address=$3
                union all select 1 from studio_outbound_email where (conversation_id=$1 or studio_thread_id=$2) and studio_normalize_email(to_address)=$3
                union all select 1 from studio_inapp_messages where thread_id=$2 and studio_normalize_email(sender_user_name)=$3 and internet_message_id is not null)
            """,c);Add(q,target.ConversationId,target.ThreadId,NormalizeAddress(sender));return (bool)q.ExecuteScalar()!;
    }
}

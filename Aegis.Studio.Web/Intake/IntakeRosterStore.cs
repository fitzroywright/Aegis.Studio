using System.Security.Claims;
using System.Text.Json;
using Aegis.Studio.Web.Media;
using Aegis.Studio.Web.Operations;
using Aegis.Studio.Web.Persistence;
using Aegis.Studio.Web.Workflow;
using Npgsql;
namespace Aegis.Studio.Web.Intake;

public sealed record IntakeRosterView(string? IntakeReference,long Revision,JsonElement Rows,Guid? JobId,JsonElement? IncomingRows,bool CanEdit=false,bool CanMessage=false);
public sealed record RosterCommit(long Revision,JsonElement Rows,bool ResolveConflict=false);
public sealed class IntakeRosterStore(StudioDatabase database)
{
    static readonly string[] Fields=["name","reference","collection","contactName","contactEmail","contactPhone","scheduledAtUtc","scheduleLocation","notes","pickupNotification"];
    public static void Validate(JsonElement rows)
    {
        if(rows.ValueKind!=JsonValueKind.Array||rows.GetArrayLength()>5000)throw new ArgumentException("Supply at most 5000 subject rows.");
        var seen=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var limits=new Dictionary<string,int>{{"name",200},{"reference",200},{"collection",100},{"contactName",200},{"contactEmail",320},{"contactPhone",100},{"scheduledAtUtc",50},{"scheduleLocation",300},{"notes",4000},{"pickupNotification",8}};
        foreach(var row in rows.EnumerateArray())
        {
            if(row.ValueKind!=JsonValueKind.Object)throw new ArgumentException("Invalid roster row.");
            foreach(var p in row.EnumerateObject())if(Fields.Contains(p.Name)&&(p.Value.ValueKind!=JsonValueKind.String||p.Value.GetString()!.Length>limits[p.Name]))throw new ArgumentException("Invalid or oversized roster field: "+p.Name);
            string name=WebsiteIntakeStore.Text(row,"name"),reference=WebsiteIntakeStore.Text(row,"reference"),email=WebsiteIntakeStore.Text(row,"contactEmail"),phone=WebsiteIntakeStore.Text(row,"contactPhone"),preference=WebsiteIntakeStore.Text(row,"pickupNotification","None");
            if(string.IsNullOrWhiteSpace(name)||name.Equals("Unclassified",StringComparison.OrdinalIgnoreCase)||string.IsNullOrWhiteSpace(reference)||!seen.Add(reference))throw new ArgumentException("Each subject needs a name and a unique stable reference. Unclassified is reserved.");
            if(!string.IsNullOrEmpty(email)&&!System.Net.Mail.MailAddress.TryCreate(email,out _))throw new ArgumentException("Correct the contact email.");
            if(preference is not ("None" or "Email" or "WhatsApp")||preference=="Email"&&string.IsNullOrWhiteSpace(email)||preference=="WhatsApp"&&!System.Text.RegularExpressions.Regex.IsMatch(phone,@"^\+\d{8,15}$"))throw new ArgumentException("Notification preferences require a valid supported contact channel.");
            var time=WebsiteIntakeStore.Text(row,"scheduledAtUtc");if(time!=""&&!DateTimeOffset.TryParse(time,out _))throw new ArgumentException("Invalid subject schedule.");
        }
    }
    public IntakeRosterView Read(Guid cv)
    {
        using var c=database.OpenConnection();
        using var q=WorkflowStore.Command(c,null,"select v.intake_reference,r.revision,r.rows::text,v.job_id from studio_conversations v left join studio_intake_rosters r on r.conversation_id=v.id where v.id=$1",cv);using var r=q.ExecuteReader();if(!r.Read())throw new KeyNotFoundException();
        string? reference=r.IsDBNull(0)?null:r.GetString(0);long revision=r.IsDBNull(1)?0:r.GetInt64(1);
        var rows=r.IsDBNull(2)?JsonSerializer.SerializeToElement(Array.Empty<object>()):JsonSerializer.Deserialize<JsonElement>(r.GetString(2));Guid? job=r.IsDBNull(3)?null:r.GetGuid(3);r.Close();
        if(job.HasValue)
        {
            string value=(string)WorkflowStore.Scalar(c,null,"""
              select coalesce(jsonb_agg(jsonb_build_object('subjectId',id,'name',name,'reference',case when reference='' then id::text else reference end,'collection',collection_name,'contactName',contact_name,'contactEmail',contact_email,'contactPhone',contact_phone,'scheduledAtUtc',coalesce(to_char(scheduled_at_utc at time zone 'UTC','YYYY-MM-DD"T"HH24:MI:SS"Z"'),''),'scheduleLocation',schedule_location,'notes',notes,'pickupNotification',pickup_notification) order by collection_name,name),'[]'::jsonb)::text from studio_job_subjects where job_id=$1 and not is_system
              """,job.Value)!;
            rows=JsonSerializer.Deserialize<JsonElement>(value);
        }
        string? conflict=WorkflowStore.Scalar(c,null,"select rows::text from studio_intake_roster_conflicts where conversation_id=$1",cv) as string;
        return new(reference,revision,rows,job,conflict is null?(JsonElement?)null:JsonSerializer.Deserialize<JsonElement>(conflict));
    }
    public object Commit(Guid cv,RosterCommit request,string actor)
    {
        Validate(request.Rows);using var c=database.OpenConnection();using var tx=c.BeginTransaction();
        WorkflowStore.Sql(c,tx,"select pg_advisory_xact_lock(hashtextextended($1,0))","intake-roster:"+cv);
        long current=Convert.ToInt64(WorkflowStore.Scalar(c,tx,"select revision from studio_intake_rosters where conversation_id=$1",cv)??0L);
        if(current!=request.Revision)throw new InvalidOperationException("Roster changed. Reload before saving.");
        WorkflowStore.Sql(c,tx,"insert into studio_intake_rosters(conversation_id,revision,rows,updated_by) values($1,$2,$3::jsonb,$4) on conflict(conversation_id) do update set revision=$2,rows=$3::jsonb,updated_by=$4,updated_at_utc=now()",cv,current+1,request.Rows.GetRawText(),actor);
        WorkflowStore.Sql(c,tx,"select studio_apply_intake_roster($1)",cv);
        if(request.ResolveConflict)WorkflowStore.Sql(c,tx,"delete from studio_intake_roster_conflicts where conversation_id=$1",cv);
        tx.Commit();return Read(cv);
    }
    public static bool Allowed(Guid cv,ClaimsPrincipal user,StudioDatabase db,WorkspaceAccessService wa,JobAccessService ja,bool edit)
    {
        using var c=db.OpenConnection();using var q=WorkflowStore.Command(c,null,"select workspace_id,job_id from studio_conversations where id=$1",cv);using var r=q.ExecuteReader();if(!r.Read())return false;
        if(!r.IsDBNull(1))return ja.HasPermission(user,r.GetGuid(1),edit?"EditJob":"ViewJob")&&(!edit||ja.HasPermission(user,r.GetGuid(1),"ReplyRequester"));
        return wa.HasPermission(user,r.GetGuid(0),edit?"ReplyRequester":"View");
    }
    public static void Map(WebApplication app)
    {
        app.MapGet("/roster-editor.js",()=>
        {
            using var input=typeof(IntakeRosterStore).Assembly.GetManifestResourceStream("Aegis.Studio.Web.Intake.RosterEditor.js")!;using var reader=new StreamReader(input);
            return Results.Text(reader.ReadToEnd(),"text/javascript; charset=utf-8");
        }).AllowAnonymous();
        var g=app.MapGroup("/api/intake").RequireAuthorization();
        g.MapGet("/lookup/{reference}",(string reference,ClaimsPrincipal user,StudioDatabase db,WorkspaceAccessService wa,JobAccessService ja)=>
        {
            using var c=db.OpenConnection();Guid? cv=WorkflowStore.Scalar(c,null,"select id from studio_conversations where intake_reference=$1",reference.ToUpperInvariant()) is Guid id?id:null;
            if(cv is null||!Allowed(cv.Value,user,db,wa,ja,false))return Results.NotFound();
            return Results.Ok(new{conversationId=cv,jobId=WorkflowStore.Scalar(c,null,"select job_id from studio_conversations where id=$1",cv.Value)});
        });
        g.MapGet("/capacity",(ClaimsPrincipal user,StudioDatabase db)=>
        {
            if(!user.IsInRole("Administrator"))return Results.Forbid();using var c=db.OpenConnection();using var q=WorkflowStore.Command(c,null,"select year,source,value from studio_intake_sequences order by year desc,source");using var r=q.ExecuteReader();var values=new List<object>();while(r.Read())values.Add(new{year=r.GetInt32(0),source=r.GetString(1),issued=r.GetInt32(2),capacity=999999,attention=r.GetInt32(2)>=950000});return Results.Ok(values);
        });
        g.MapGet("/{cv:guid}/roster",(Guid cv,ClaimsPrincipal user,StudioDatabase db,WorkspaceAccessService wa,JobAccessService ja)=>
        {
            if(!Allowed(cv,user,db,wa,ja,false))return Results.Forbid();var view=new IntakeRosterStore(db).Read(cv);
            return Results.Ok(view with{CanEdit=Allowed(cv,user,db,wa,ja,true),CanMessage=view.JobId.HasValue&&ja.HasPermission(user,view.JobId.Value,"ReplyRequester")});
        });
        g.MapPut("/{cv:guid}/roster",(Guid cv,RosterCommit request,ClaimsPrincipal user,StudioDatabase db,WorkspaceAccessService wa,JobAccessService ja)=>
        {
            if(!Allowed(cv,user,db,wa,ja,true))return Results.Forbid();
            try{return Results.Ok(new IntakeRosterStore(db).Commit(cv,request,user.Identity?.Name??"Staff"));}catch(ArgumentException e){return Results.BadRequest(new{error=e.Message});}catch(InvalidOperationException e){return Results.Conflict(new{error=e.Message});}
        });
        g.MapPost("/{cv:guid}/roster-file",async(Guid cv,HttpRequest request,ClaimsPrincipal user,StudioDatabase db,WorkspaceAccessService wa,JobAccessService ja,CancellationToken ct)=>
        {
            if(!Allowed(cv,user,db,wa,ja,true))return Results.Forbid();
            var f=await request.ReadFormAsync(ct);if(f.Files.Count!=1||f.Files[0].Length>5_000_000)return Results.BadRequest(new{error="Choose one CSV or XLSX file, at most 5 MB."});
            try{await using var s=f.Files[0].OpenReadStream();string csv=RosterFileReader.Read(s,f.Files[0].FileName);return Results.Ok(new{cells=MediaProductionRules.ReadRoster(csv)});}catch(ArgumentException e){return Results.BadRequest(new{error=e.Message});}
        }).DisableAntiforgery();
        g.MapGet("/{cv:guid}/status",(Guid cv,ClaimsPrincipal user,StudioDatabase db,Email.StudioInquiryAssociations associations)=>
        {
            if(!associations.CanAccess(cv,user))return Results.Forbid();using var c=db.OpenConnection();
            var reference=WorkflowStore.Scalar(c,null,"select intake_reference from studio_conversations where id=$1",cv);
            var files=new List<object>();using(var f=WorkflowStore.Command(c,null,"select id,metadata->>'fileName',state from studio_intake_attachments where conversation_id=$1",cv))using(var fr=f.ExecuteReader())while(fr.Read())files.Add(new{id=fr.GetGuid(0),fileName=fr.GetString(1),state=fr.GetString(2)});
            var rosterConflict=WorkflowStore.Scalar(c,null,"select exists(select 1 from studio_intake_roster_conflicts where conversation_id=$1)",cv);
            using var q=WorkflowStore.Command(c,null,"select id,status,last_error,provider_accepted_at_utc,attempts from studio_outbound_email where conversation_id=$1 order by created_at_utc desc",cv);using var r=q.ExecuteReader();var deliveries=new List<object>();while(r.Read())deliveries.Add(new{id=r.GetGuid(0),status=r.GetString(1),error=r.IsDBNull(2)?null:r.GetString(2),providerAcceptedAtUtc=r.IsDBNull(3)?(DateTimeOffset?)null:r.GetFieldValue<DateTimeOffset>(3),attempts=r.GetInt32(4)});return Results.Ok(new{intakeReference=reference,deliveries,attachments=files,rosterConflict,acknowledgmentStatus=deliveries.Count==0?"NotQueued — check contact and workspace sender":"See delivery details"});
        });
    }
}

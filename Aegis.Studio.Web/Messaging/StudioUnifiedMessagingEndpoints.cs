using System.Security.Claims;
using System.Text.Json;
using Aegis.Studio.Web.Operations;
using Aegis.Studio.Web.Persistence;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using Npgsql;
using static Aegis.Studio.Web.Messaging.StudioUnifiedMessaging;

namespace Aegis.Studio.Web.Messaging;

public sealed record StudioInboxThread(Guid Id, string ScopeType, Guid? ScopeId, string Title, string Kind);
public sealed record StudioInboxRow(StudioInboxThread Thread, StudioInAppMessage Message);
public sealed record StudioInboxPage(IReadOnlyList<StudioInboxRow> Items, long Total, int Offset, int Limit);
public sealed record CreateMessagingContact(string DisplayName, string EmailAddress);
public sealed record RetryStudioDelivery(bool ReconciledNotSent = false);

public sealed class StudioUnifiedInbox(StudioDatabase database, WorkspaceAccessService workspaceAccess, JobAccessService jobAccess)
{
    public StudioInboxPage Get(ClaimsPrincipal user, int offset, int limit, string? query, Guid? jobId)
    {
        offset=Math.Max(0,offset);limit=Math.Clamp(limit,1,200);
        string current=CurrentUser(user);
        Guid[] workspaces=workspaceAccess.GetAccessibleWorkspaceIds(user).Where(id=>workspaceAccess.HasPermission(user,id,"View")).ToArray();
        var permissions=jobAccess.GetPermissionMap(user);
        Guid[] jobs=permissions.Where(p=>p.Value.ViewJob).Select(p=>p.Key).ToArray();
        Guid[] conversations=permissions.Where(p=>p.Value.ViewConversation).Select(p=>p.Key).ToArray();
        using var c=database.OpenConnection();using var q=new NpgsqlCommand("""
            with inbox as (
                select m.id,m.thread_id,m.sender_user_name,m.body,m.created_at_utc,m.subject,m.recipient_addresses,m.internet_message_id,
                       t.scope_type,t.scope_id,t.title,'Thread'::text as kind
                from studio_inapp_messages m join studio_message_threads t on t.id=m.thread_id
                where (t.scope_type='Direct' and exists(select 1 from studio_message_thread_members tm where tm.thread_id=t.id and lower(tm.user_name)=lower($1)))
                   or (t.scope_type='Workspace' and t.scope_id=any($2)) or (t.scope_type='Job' and t.scope_id=any($3))
                union all
                select m.id,c.id,coalesce(m.author_user_name,m.author_display_name),m.body,m.created_at_utc,c.subject,
                       array[c.contact_address],m.external_message_id,case when c.job_id is null then 'Workspace' else 'Job' end,
                       coalesce(c.job_id,c.workspace_id),coalesce(j.title,c.subject),'Conversation'
                from studio_messages m join studio_conversations c on c.id=m.conversation_id left join studio_jobs j on j.id=c.job_id
                where c.workspace_id=any($2) or (c.job_id=any($4) and m.visibility='External')
            )
            select *,count(*) over() from inbox
            where ($5::uuid is null or (scope_type='Job' and scope_id=$5))
              and ($6='' or strpos(lower(subject||' '||body||' '||sender_user_name||' '||title||' '||array_to_string(recipient_addresses,' ')),$6)>0)
            order by created_at_utc desc,id desc limit $7 offset $8
            """,c);
        Add(q,current,workspaces,jobs,conversations,jobId,query?.Trim().ToLowerInvariant()??"",limit,offset);
        using var r=q.ExecuteReader();List<StudioInboxRow> rows=[];long total=0;
        while(r.Read())
        {
            rows.Add(new(new(r.GetGuid(1),r.GetString(8),r.IsDBNull(9)?null:r.GetGuid(9),r.GetString(10),r.GetString(11)),
                new(r.GetGuid(0),r.GetGuid(1),r.GetString(2),r.GetString(3),r.GetFieldValue<DateTimeOffset>(4),r.GetString(5),r.GetFieldValue<string[]>(6),r.IsDBNull(7)?null:r.GetString(7))));total=r.GetInt64(12);
        }
        return new(rows,total,offset,limit);
    }
}

public static class StudioUnifiedMessagingEndpoints
{
    private static readonly JsonSerializerOptions Json=new(JsonSerializerDefaults.Web);
    public static void MapStudioUnifiedMessaging(this WebApplication app)
    {
        app.Services.GetRequiredService<StudioUnifiedMessaging>().EnsureSchema();
        var group=app.MapGroup("/api/messages").RequireAuthorization();
        group.MapGet("/address-book",(ClaimsPrincipal user,StudioAddressBook book)=>Results.Ok(book.GetAll(user)));
        group.MapPost("/contacts",(CreateMessagingContact request,ClaimsPrincipal user,StudioAddressBook book)=>Execute(()=>Results.Ok(book.Get(book.GetOrCreateExternal(request.DisplayName,request.EmailAddress).Id,user))));
        group.MapGet("/identity",(ClaimsPrincipal user,StudioAddressBook book)=>Results.Ok(book.GetAll(user).FirstOrDefault(e=>string.Equals(e.UserName,CurrentUser(user),StringComparison.OrdinalIgnoreCase))));
        group.MapGet("/inbox",(int? offset,int? limit,string? query,Guid? jobId,ClaimsPrincipal user,StudioUnifiedInbox inbox)=>Results.Ok(inbox.Get(user,offset??0,limit??100,query,jobId)));
        group.MapGet("/threads/{threadId:guid}/deliveries",(Guid threadId,ClaimsPrincipal user,StudioUnifiedMessaging router)=>Execute(()=>Results.Ok(router.GetDeliveries(threadId,user))));
        group.MapGet("/threads/{threadId:guid}/recipients",(Guid threadId,ClaimsPrincipal user,StudioInAppMessageStore messages,StudioAddressBook book,StudioDatabase db)=>Execute(()=>
        {
            messages.AssertThreadAccess(threadId,user);
            using var c=db.OpenConnection();using var q=new NpgsqlCommand("""
                select destination_id from studio_message_route_recipients where message_id=(
                    select m.id from studio_inapp_messages m where m.thread_id=$1
                    and exists(select 1 from studio_message_route_recipients r where r.message_id=m.id)
                    order by m.created_at_utc desc limit 1)
                """,c);Add(q,threadId);using var r=q.ExecuteReader();List<Guid> ids=[];while(r.Read())ids.Add(r.GetGuid(0));r.Close();
            var available=book.GetAll(user);var recipients=available.Where(e=>ids.Contains(e.Id)).ToArray();
            if(recipients.Length==0)
            {
                var names=messages.GetThreadParticipants(threadId).Where(n=>!string.Equals(n,CurrentUser(user),StringComparison.OrdinalIgnoreCase)).ToHashSet(StringComparer.OrdinalIgnoreCase);
                recipients=available.Where(e=>e.UserName is not null&&names.Contains(e.UserName)).ToArray();
            }
            return Results.Ok(recipients);
        }));
        group.MapPost("/send",async(HttpRequest http,ClaimsPrincipal user,StudioUnifiedMessaging router,StudioInAppMessageStore messages,IHubContext<StudioMessageHub> hub,CancellationToken token)=>
        {
            try
            {
                if(!http.HasFormContentType)return Results.BadRequest(new{error="A multipart message form is required."});
                var form=await http.ReadFormAsync(token);
                var request=JsonSerializer.Deserialize<UnifiedSendRequest>(form["message"].ToString(),Json)??throw new InvalidOperationException("Message details are required.");
                var result=await router.SendAsync(request,form.Files,user,token);
                foreach(var participant in messages.GetThreadParticipants(result.ThreadId))
                    await hub.Clients.Group(StudioMessageHub.UserGroup(participant)).SendAsync("threadChanged",result.ThreadId,token);
                return Results.Ok(result);
            }
            catch(JsonException){return Results.BadRequest(new{error="Invalid message details."});}
            catch(UnauthorizedAccessException){return Results.Forbid();}
            catch(KeyNotFoundException){return Results.NotFound();}
            catch(InvalidOperationException ex){return Results.BadRequest(new{error=ex.Message});}
        }).DisableAntiforgery();
        group.MapPost("/{messageId:guid}/retry-routing",async(Guid messageId,ClaimsPrincipal user,StudioUnifiedMessaging router,CancellationToken token)=>
        {
            try{return Results.Ok(await router.RetryRoutingAsync(messageId,user,token));}
            catch(UnauthorizedAccessException){return Results.Forbid();}catch(KeyNotFoundException){return Results.NotFound();}
            catch(InvalidOperationException ex){return Results.BadRequest(new{error=ex.Message});}
        });
        group.MapPost("/deliveries/{deliveryId:guid}/retry",(Guid deliveryId,RetryStudioDelivery request,ClaimsPrincipal user,StudioUnifiedMessaging router)=>Execute(()=>
        {router.RetryDelivery(deliveryId,user,request.ReconciledNotSent);return Results.Accepted();}));
        var admin=app.MapGroup("/api/admin/messaging").RequireAuthorization(new AuthorizeAttribute{Roles="Administrator"});
        admin.MapGet("/address-book",(StudioAddressBook book)=>Results.Ok(book.GetAll()));
        admin.MapGet("/unresolved",(Aegis.Studio.Web.Email.UnroutedEmailStore store)=>Results.Ok(store.GetOpen()));
        admin.MapPost("/unresolved/{id:guid}/reprocess",async(Guid id,StudioUnifiedInbound inbound,Aegis.Studio.Web.Email.UnroutedEmailStore store,StudioDatabase db,CancellationToken token)=>
        {
            var item=store.Get(id);if(item is null)return Results.NotFound();
            try
            {
                if(!await inbound.TryImportAsync(item.ToInboundEmail(),token))return Results.UnprocessableEntity(new{error="No registered Studio destination or verified reply correlation matched. Correct routing configuration first."});
                using var c=db.OpenConnection();using var q=new NpgsqlCommand("update studio_unrouted_email set resolved_at_utc=now() where id=$1",c);Add(q,id);q.ExecuteNonQuery();return Results.Ok(new{resolved=true});
            }
            catch(InvalidOperationException ex){return Results.BadRequest(new{error=ex.Message});}
        });
        admin.MapPut("/address-book/{id:guid}",(Guid id,StudioAddressEntry entry,StudioAddressBook book)=>Execute(()=>
            id!=entry.Id?Results.BadRequest(new{error="Route identity mismatch."}):Results.Ok(book.Save(entry))));
    }
    private static IResult Execute(Func<IResult> action)
    {
        try{return action();}catch(UnauthorizedAccessException){return Results.Forbid();}catch(KeyNotFoundException){return Results.NotFound();}
        catch(InvalidOperationException ex){return Results.BadRequest(new{error=ex.Message});}
        catch(PostgresException ex) when(ex.SqlState=="23505"){return Results.Conflict(new{error="This address is already registered. Refresh the address book."});}
    }
}

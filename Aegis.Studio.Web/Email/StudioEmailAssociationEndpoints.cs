using System.Security.Claims;
using Aegis.Studio.Web.Operations;
using Aegis.Studio.Web.Persistence;
using Microsoft.AspNetCore.Authorization;
using Npgsql;
using static Aegis.Studio.Web.Messaging.StudioUnifiedMessaging;

namespace Aegis.Studio.Web.Email;

public static class StudioEmailAssociationEndpoints
{
    public sealed record VerifySentEmail(DateTimeOffset? SentAfterUtc=null);
    public static void MapStudioEmailAssociations(this WebApplication app)
    {
        app.Services.GetRequiredService<StudioInquiryAssociations>().EnsureSchema();
        app.MapPost("/api/email/outbox/{delivery:guid}/verify-provider-id",async(Guid delivery,VerifySentEmail request,ClaimsPrincipal user,
            OutboundEmailStore outbox,StudioInquiryAssociations associations,StudioGraphSentMessageVerifier verifier,IConfiguration configuration,CancellationToken token)=>
        {
            try
            {
                var email=outbox.GetDelivery(delivery)??throw new KeyNotFoundException();
                if(email.StudioMessageId.HasValue){if(!associations.CanReviewThread(email.ConversationId,user))throw new UnauthorizedAccessException();}
                else associations.AssertAccess(email.ConversationId,user,true);
                if(email.DeliveryChannel!="Email" || email.Status is not ("ProviderAccepted" or "Attention"))
                    throw new InvalidOperationException("Only submitted or held email can be verified. This action never sends email.");
                var metadata=System.Text.Json.JsonSerializer.Deserialize<Dictionary<string,string>>(email.DeliveryMetadata)??new();
                var verified=await verifier.VerifyAsync(email,metadata.GetValueOrDefault("Studio.MessageId",email.Id.ToString("D")),
                    metadata.GetValueOrDefault("Email.ReplyTo",configuration["Messaging:MicrosoftGraph:Email:SenderUpn"]??""),
                    request.SentAfterUtc??(email.ProviderAcceptedAtUtc??email.CreatedAt).AddMinutes(-5),token);
                outbox.RecordVerifiedInternetId(delivery,null,verified);return Results.Ok(verified);
            }
            catch(UnauthorizedAccessException){return Results.Forbid();}catch(KeyNotFoundException){return Results.NotFound();}
            catch(InvalidOperationException ex){return Results.BadRequest(new{error=ex.Message});}
            catch(HttpRequestException){return Results.UnprocessableEntity(new{error="Microsoft 365 sent-item verification failed. No email was sent."});}
        }).RequireAuthorization(new AuthorizeAttribute{Roles="Administrator"});
        var group=app.MapGroup("/api/conversations/{id:guid}/association").RequireAuthorization();
        group.MapGet("",(Guid id,ClaimsPrincipal user,StudioInquiryAssociations store)=>Execute(()=>Results.Ok(store.Get(id,user))));
        group.MapGet("/clients",(Guid id,string? query,ClaimsPrincipal user,StudioInquiryAssociations store)=>Execute(()=>Results.Ok(store.Search(id,user,query))));
        group.MapPut("/client",(Guid id,ChangeInquiryClient request,ClaimsPrincipal user,StudioInquiryAssociations store)=>Execute(()=>{store.SetClient(id,request.ClientId,user);return Results.Ok(store.Get(id,user));}));
        group.MapPut("/job",(Guid id,ChangeInquiryJob request,ClaimsPrincipal user,StudioInquiryAssociations store)=>Execute(()=>{store.SetJob(id,request.JobId,user);return Results.Ok();}));
        group.MapPost("/contacts",(Guid id,AddInquiryContact request,ClaimsPrincipal user,StudioInquiryAssociations store)=>Execute(()=>{store.AddContact(id,request.Address,user);return Results.Ok();}));
        group.MapPost("/messages/{message:guid}/move",(Guid id,Guid message,MoveInquiryMessage request,ClaimsPrincipal user,StudioInquiryAssociations store)=>Execute(()=>Results.Ok(new{conversationId=store.MoveMessage(id,message,request,user)})));
        app.MapGet("/api/email/routing-review",(ClaimsPrincipal user,UnroutedEmailStore unresolved,StudioInquiryAssociations store,WorkspaceAccessService workspaces,StudioDatabase db)=>Execute(()=>
        {
            using var c=db.OpenConnection();using var q=new NpgsqlCommand("select id,candidate_conversations,candidate_threads,routing_workspace_id from studio_unrouted_email where resolved_at_utc is null",c);
            using var r=q.ExecuteReader();List<Guid> visible=[];
            while(r.Read())
            {
                var candidates=r.GetFieldValue<Guid[]>(1);var threads=r.GetFieldValue<Guid[]>(2);
                bool allowed=candidates.Length+threads.Length>0
                    ?candidates.All(id=>store.CanAccess(id,user,true))&&threads.All(id=>store.CanReviewThread(id,user))
                    :!r.IsDBNull(3)?workspaces.HasPermission(user,r.GetGuid(3),"ReplyRequester"):user.IsInRole("Administrator");
                if(allowed)visible.Add(r.GetGuid(0));
            }
            return Results.Ok(unresolved.GetOpen().Where(x=>visible.Contains(x.Id)));
        })).RequireAuthorization();
        app.MapPost("/api/email/routing-review/{review:guid}/confirm",async(Guid review,ConfirmEmailRouting request,ClaimsPrincipal user,
            UnroutedEmailStore unresolved,StudioInquiryAssociations store,StudioEmailIntake intake,WorkspaceAccessService workspaces,StudioStore studio,StudioDatabase db,CancellationToken token)=>
        {
            try
            {
                var item=unresolved.Get(review)??throw new KeyNotFoundException();
                Guid? target=request.JobId.HasValue?store.ConversationForJob(request.JobId.Value,user):request.ConversationId;
                Guid? workspace=request.WorkspaceId;
                // Read permission to the review's source AND edit permission to its destination.
                using(var c=db.OpenConnection())using(var q=new NpgsqlCommand("select candidate_conversations,candidate_threads,routing_workspace_id,resolved_at_utc from studio_unrouted_email where id=$1",c))
                {
                    Add(q,review);using var r=q.ExecuteReader();r.Read();
                    if(!r.IsDBNull(3))throw new InvalidOperationException("This email was already reviewed. Refresh the inbox.");
                    var sources=r.GetFieldValue<Guid[]>(0);var threads=r.GetFieldValue<Guid[]>(1);
                    foreach(var source in sources)store.AssertAccess(source,user,true);
                    if(!threads.All(id=>store.CanReviewThread(id,user)))throw new UnauthorizedAccessException();
                    if(sources.Length==0 && threads.Length==0 && !r.IsDBNull(2) && !workspaces.HasPermission(user,r.GetGuid(2),"ReplyRequester"))throw new UnauthorizedAccessException();
                    if(sources.Length==0 && threads.Length==0 && r.IsDBNull(2) && !user.IsInRole("Administrator"))throw new UnauthorizedAccessException();
                }
                if(target.HasValue){store.AssertAccess(target.Value,user,true);workspace=studio.GetConversation(target.Value)!.WorkspaceId;}
                else if(!request.NewInquiry||!workspace.HasValue||!workspaces.HasPermission(user,workspace.Value,"ReplyRequester"))throw new UnauthorizedAccessException();
                var result=await intake.ReceiveAsync(item.ToInboundEmail(),token,
                    fallback:new(true,workspace,null,null,null),confirmed:target.HasValue?new(target,null):null,
                    actor:CurrentUser(user),confirmNewInquiry:!target.HasValue);
                target=result.ConversationId;
                if(!result.Routed)return Results.UnprocessableEntity(result);
                if(request.AddContact)store.AddContact(target??throw new InvalidOperationException("The confirmed inquiry is unavailable."),item.FromAddress,user);
                unresolved.MarkResolved(review,workspace!.Value,"Staff confirmed");return Results.Ok(result);
            }
            catch(UnauthorizedAccessException){return Results.Forbid();}catch(KeyNotFoundException){return Results.NotFound();}
            catch(InvalidOperationException ex){return Results.BadRequest(new{error=ex.Message});}
        }).RequireAuthorization();
    }
    private static IResult Execute(Func<IResult> action)
    {try{return action();}catch(UnauthorizedAccessException){return Results.Forbid();}catch(KeyNotFoundException){return Results.NotFound();}catch(InvalidOperationException ex){return Results.BadRequest(new{error=ex.Message});}}
}

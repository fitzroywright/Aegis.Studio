using System.Security.Claims;
using Aegis.Studio.Web.Operations;
namespace Aegis.Studio.Web.Reminders;

public static class ClientReminderEndpoints
{
    public static void MapClientReminders(this WebApplication app)
    {
        app.Services.GetRequiredService<ClientReminderStore>().EnsureSchema();
        app.MapGet("/api/client-reminders",(ClaimsPrincipal user,ClientReminderStore store,JobAccessService access)=>Results.Ok(store.List(null,user,access))).RequireAuthorization();
        var group=app.MapGroup("/api/jobs/{job:guid}/client-reminders").RequireAuthorization();
        group.MapGet("",(Guid job,ClaimsPrincipal user,ClientReminderStore store,JobAccessService access)=>Run(job,user,access,false,()=>Results.Ok(new{items=store.List(job,user,access),sources=store.Sources(job).Where(s=>s.Kind!="Deposit"||access.HasPermission(user,job,"ViewFinancials")),preview=store.Preview(job),preferences=store.Preferences(job),rules=store.Rules(job).Where(r=>r.Kind!="Deposit"||access.HasPermission(user,job,"ViewFinancials")),templates=ClientReminderRules.Templates,canManage=access.HasPermission(user,job,"ReplyRequester")})));
        group.MapGet("/{id:guid}/history",(Guid job,Guid id,ClaimsPrincipal user,ClientReminderStore store,JobAccessService access)=>Run(job,user,access,false,()=>store.KindOf(job,id)=="Deposit"&&!access.HasPermission(user,job,"ViewFinancials")?Results.Forbid():Results.Ok(store.History(job,id))));
        group.MapPut("/preferences",(Guid job,ReminderPreferences request,ClaimsPrincipal user,ClientReminderStore store,JobAccessService access)=>Run(job,user,access,true,()=>{store.SavePreferences(job,request,ClientReminderStore.Actor(user));return Results.NoContent();}));
        group.MapPut("/rules",(Guid job,ReminderRuleRequest request,ClaimsPrincipal user,ClientReminderStore store,JobAccessService access)=>Run(job,user,access,true,()=>{if(request.Kind=="Deposit"&&!access.HasPermission(user,job,"ViewFinancials"))return Results.Forbid();store.SaveRule(job,request,ClientReminderStore.Actor(user));return Results.NoContent();}));
        group.MapPost("",(Guid job,ReminderRequest request,ClaimsPrincipal user,ClientReminderStore store,JobAccessService access)=>Run(job,user,access,true,()=>request.Kind=="Deposit"&&!access.HasPermission(user,job,"ViewFinancials")?Results.Forbid():Results.Ok(new{id=store.Create(job,request,ClientReminderStore.Actor(user))})));
        group.MapPost("/{id:guid}",(Guid job,Guid id,ReminderChange request,ClaimsPrincipal user,ClientReminderStore store,JobAccessService access)=>Run(job,user,access,true,()=>{if(store.KindOf(job,id)=="Deposit"&&!access.HasPermission(user,job,"ViewFinancials"))return Results.Forbid();store.Change(job,id,request,ClientReminderStore.Actor(user));return Results.NoContent();}));
    }
    static IResult Run(Guid job,ClaimsPrincipal user,JobAccessService access,bool write,Func<IResult> action)
    {
        if(!access.HasPermission(user,job,write?"ReplyRequester":"ViewConversation"))return Results.Forbid();
        try{return action();}catch(ArgumentException e){return Results.BadRequest(new{error=e.Message});}catch(KeyNotFoundException){return Results.NotFound();}
    }
}
public sealed class ClientReminderWorker(ClientReminderStore store,ILogger<ClientReminderWorker> logger):BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken token)
    {
        using var timer=new PeriodicTimer(TimeSpan.FromMinutes(1));
        while(!token.IsCancellationRequested)
        {
            try{store.Tick();}catch(Exception e){logger.LogError(e,"Client reminder scheduling failed");}
            try{if(!await timer.WaitForNextTickAsync(token))break;}catch(OperationCanceledException)when(token.IsCancellationRequested){break;}
        }
    }
}

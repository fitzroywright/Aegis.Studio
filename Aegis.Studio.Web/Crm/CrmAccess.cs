using System.Security.Claims;
using Aegis.Studio.Web.Persistence;
using Aegis.Studio.Web.Operations;
using Npgsql;

namespace Aegis.Studio.Web.Crm;

public static class CrmPermissions
{
    public const string View="studio.crm.view",Create="studio.crm.create",Edit="studio.crm.edit",Merge="studio.crm.merge",Archive="studio.crm.archive",Communications="studio.crm.communications.view",Send="studio.crm.communications.send",Tasks="studio.crm.tasks.manage",Financials="studio.crm.financials.view",Automations="studio.crm.automations.manage";
    public static readonly string[] All=[View,Create,Edit,Merge,Archive,Communications,Send,Tasks,Financials,Automations];
}

public sealed class CrmAccess(StudioDatabase database,ClaimsPrincipal user)
{
    public static string Name(ClaimsPrincipal user)=>user.FindFirstValue("login_name")??user.Identity?.Name??throw new UnauthorizedAccessException();
    public bool Has(string permission)=>user.IsInRole("Administrator")||new GlobalOperationalRoleStore(database).HasPermission(user,permission);
    public void Demand(string permission){if(!Has(permission))throw new UnauthorizedAccessException();}
    private Guid[]? workspaces;
    public Guid[] Workspaces=>workspaces??=ReadWorkspaces();
    private Guid[] ReadWorkspaces()
    {
        using var c=database.OpenConnection();using var q=new NpgsqlCommand("select id from studio_workspaces where $1 or exists(select 1 from studio_workspace_members m where m.workspace_id=studio_workspaces.id and lower(m.user_name)=lower($2) and m.is_active and m.access_workspace and m.can_view)",c);
        q.Parameters.AddWithValue(IsAdministrator);q.Parameters.AddWithValue(Name(user));using var r=q.ExecuteReader();var ids=new List<Guid>();while(r.Read())ids.Add(r.GetGuid(0));return ids.ToArray();
    }
    public Guid[] Jobs=>new JobAccessService(database).GetAccessibleJobIds(user).ToArray();
    public bool IsAdministrator=>user.IsInRole("Administrator");
    public void Workspace(Guid id){if(!IsAdministrator&&!Workspaces.Contains(id))throw new UnauthorizedAccessException();}
    public void Client(Guid id,string permission=CrmPermissions.View)
    {
        Demand(CrmPermissions.View);Demand(permission);using var c=database.OpenConnection();using var q=new NpgsqlCommand("select exists(select 1 from studio_clients c where c.id=$1 and "+Scope+")",c);
        q.Parameters.AddWithValue(id);q.Parameters.AddWithValue(IsAdministrator);q.Parameters.AddWithValue(Workspaces);
        if(q.ExecuteScalar() is not true)throw new KeyNotFoundException();
    }
    // A primary workspace restricts an identity even when other jobs reference it.
    public const string Scope="($2 or c.primary_workspace_id=any($3) or (c.primary_workspace_id is null and (exists(select 1 from studio_jobs j where j.client_id=c.id and j.workspace_id=any($3)) or exists(select 1 from studio_conversations e where e.client_id=c.id and e.workspace_id=any($3)))))";
}

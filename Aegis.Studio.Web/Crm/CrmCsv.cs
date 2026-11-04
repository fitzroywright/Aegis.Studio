using System.Text;
using System.Text.Json;
using Npgsql;
namespace Aegis.Studio.Web.Crm;
public sealed record CrmImportRequest(string Csv,Guid WorkspaceId);
public static class CrmCsv
{
    public static string Cell(string? value)
    {
        value??="";if(value.TrimStart().FirstOrDefault() is '=' or '+' or '-' or '@' or '\t' or '\r')value="'"+value;
        return "\""+value.Replace("\"","\"\"")+"\"";
    }
    public static List<string[]> Parse(string csv)
    {
        if(string.IsNullOrEmpty(csv)||csv.Length>1_000_000)throw new ArgumentException("CSV is limited to 1 MB and 500 clients.");
        var rows=new List<string[]>();var row=new List<string>();var field=new StringBuilder();bool quoted=false,closed=false;
        for(int i=0;i<csv.Length;i++)
        {
            char ch=csv[i];
            if(quoted){if(ch=='"'){if(i+1<csv.Length&&csv[i+1]=='"'){field.Append('"');i++;}else{quoted=false;closed=true;}}else field.Append(ch);continue;}
            if(ch=='"'&&field.Length==0&&!closed){quoted=true;continue;}
            if(ch is ',' or '\r' or '\n')
            {
                row.Add(field.ToString());field.Clear();closed=false;
                if(ch!=','){if(ch=='\r'&&i+1<csv.Length&&csv[i+1]=='\n')i++;if(row.Any(x=>x.Length>0))rows.Add(row.ToArray());row.Clear();}
                if(rows.Count>501||row.Count>30)throw new ArgumentException("CSV is limited to 500 clients and 30 columns.");continue;
            }
            if(closed||ch=='"')throw new ArgumentException("Malformed CSV quoting.");field.Append(ch);
        }
        if(quoted)throw new ArgumentException("Unterminated CSV field.");
        if(field.Length>0||row.Count>0||closed){row.Add(field.ToString());rows.Add(row.ToArray());}
        if(rows.Count>501)throw new ArgumentException("CSV is limited to 500 clients.");return rows;
    }
}
public sealed partial class CrmStore
{
    public string Export(CrmQuery query)
    {
        access.Demand(CrmPermissions.Export);var first=Directory(query with{Page=1,PageSize=200});if(first.GetProperty("total").GetInt32()>10000)throw new ArgumentException("Filter the export to 10000 or fewer clients.");
        var output=new StringBuilder("\uFEFFDisplay Name,Client Type,Telephone,Email,Primary Workspace,Total Jobs,Last Job,Lifetime Value JMD,Last Contact,Next Follow-up,Relationship Status\r\n");
        int pages=(first.GetProperty("total").GetInt32()+199)/200;var exportedIds=new List<Guid>();
        for(int page=1;page<=pages;page++)foreach(var client in (page==1?first:Directory(query with{Page=page,PageSize=200})).GetProperty("items").EnumerateArray())
        {
            exportedIds.Add(client.GetProperty("id").GetGuid());
            output.AppendLine(string.Join(',',new[]{"displayName","clientType","phone","email","primaryWorkspace","totalJobs","lastJob","lifetimeValue","lastContact","nextFollowUp","relationshipStatus"}.Select(key=>CrmCsv.Cell(client.GetProperty(key).ToString()))));
        }
        using var c=database.OpenConnection();using var tx=c.BeginTransaction();
        Execute(c,tx,"insert into studio_crm_activity(id,client_id,workspace_id,event_type,description,actor) select gen_random_uuid(),id,primary_workspace_id,'ClientDataExported','Client included in a filtered CSV export.',$2 from studio_clients where id=any($1)",exportedIds.ToArray(),Actor);tx.Commit();
        return output.ToString();
    }
    private CrmClientRequest[] ParseImport(CrmImportRequest request)
    {
        access.Demand(CrmPermissions.Create);access.Workspace(request.WorkspaceId);var rows=CrmCsv.Parse(request.Csv);
        if(rows.Count<2)throw new ArgumentException("CSV needs a header and at least one client.");
        string[] header=rows[0].Select(x=>x.Trim().TrimStart('\uFEFF').ToLowerInvariant()).ToArray();
        if(!header.Contains("display name")||header.Distinct().Count()!=header.Length)throw new ArgumentException("Use unique column names including Display Name, Email, Telephone, Client Type and Organisation Name.");
        var clients=new List<CrmClientRequest>();var keys=new HashSet<string>();
        foreach(var row in rows.Skip(1))
        {
            if(row.Length!=header.Length)throw new ArgumentException("All CSV rows must match the header width.");
            string? Value(string name){int index=Array.IndexOf(header,name);return index<0||string.IsNullOrWhiteSpace(row[index])?null:row[index].Trim();}
            var client=new CrmClientRequest(Value("display name")??"",Value("email"),Value("telephone"),Value("client type")??"Individual",OrganisationName:Value("organisation name"),PrimaryWorkspaceId:request.WorkspaceId,Tags:["CSV import"]);CrmRules.Validate(client);
            foreach(var key in new[]{client.Email is null?null:"email:"+CrmRules.NormalizeEmail(client.Email),client.Phone is null?null:"phone:"+CrmRules.NormalizePhone(client.Phone)}.Where(k=>k is not null))if(!keys.Add(key!))throw new ArgumentException("Duplicate email or telephone within the import. Review before importing.");
            clients.Add(client);
        }
        return clients.ToArray();
    }
    public object ImportPreview(CrmImportRequest request)
    {
        var clients=ParseImport(request);using var c=database.OpenConnection();using var tx=c.BeginTransaction();
        foreach(var r in clients)if(Scalar(c,tx,"select id from studio_clients where merged_into_id is null and ((nullif(lower(btrim(email)),'')=$1 and $1<>'') or (nullif(studio_crm_normalize_phone(phone),'')=$2 and $2<>'')) limit 1",CrmRules.NormalizeEmail(r.Email),CrmRules.NormalizePhone(r.Phone)) is not null)throw new ArgumentException("An imported email or telephone already exists. Review duplicate identities before importing.");
        return new{count=clients.Length,clients};
    }
    public object Import(CrmImportRequest request)
    {
        _=ImportPreview(request);var clients=ParseImport(request);var ids=new List<Guid>();
        // One transaction for the entire import; failed validation or SQL rolls everything back.
        using var c=database.OpenConnection();using var tx=c.BeginTransaction(System.Data.IsolationLevel.Serializable);
        foreach(var r in clients)
        {
            if(Scalar(c,tx,"select id from studio_clients where merged_into_id is null and ((nullif(lower(btrim(email)),'')=$1 and $1<>'') or (nullif(studio_crm_normalize_phone(phone),'')=$2 and $2<>'')) limit 1",CrmRules.NormalizeEmail(r.Email),CrmRules.NormalizePhone(r.Phone)) is not null)throw new InvalidOperationException("A matching identity was added. Review a fresh import preview.");
            Guid id=Guid.NewGuid();Execute(c,tx,"insert into studio_clients(id,display_name,email,phone,client_type,organisation_name,primary_workspace_id,tags) values($1,$2,$3,$4,$5,$6,$7,$8)",id,r.DisplayName,r.Email,r.Phone,r.ClientType,r.OrganisationName,r.PrimaryWorkspaceId,r.Tags);Audit(c,tx,id,r.PrimaryWorkspaceId,"ClientCreated","Client imported from CSV.",Actor);ids.Add(id);
        }
        tx.Commit();return new{count=ids.Count,ids};
    }
}

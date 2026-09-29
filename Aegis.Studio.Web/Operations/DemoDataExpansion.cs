using System.Security.Cryptography;
using Npgsql;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace Aegis.Studio.Web.Operations;

// Only deterministic DEMO identifiers are used; no real jobs or files are changed.
internal static class DemoDataExpansion
{
    internal static readonly int[] JobNumbers = [1,2,4,5,7,8];
    internal static Guid Id(int group,int number) => Guid.Parse($"de000000-0000-4000-{group:0000}-{number:000000000000}");

    internal static bool Seed(NpgsqlConnection connection,NpgsqlTransaction transaction,string login,IConfiguration config,IWebHostEnvironment environment)
    {
        using(var check=new NpgsqlCommand("select exists(select 1 from studio_state where key='demo-data-v2')",connection,transaction))
            if((bool)check.ExecuteScalar()!)return false;
        string configured=config["Storage:VersionStorePath"] ?? "";
        string root=Path.GetFullPath(string.IsNullOrWhiteSpace(configured)?Path.Combine(environment.ContentRootPath,"VersionStore"):configured);
        DateTimeOffset today=new(DateTime.UtcNow.Date,TimeSpan.Zero);
        string[] phases=["Prepare equipment","Capture portraits","Retouch selected images","Review and delivery"];
        string[] types=["EquipmentPrep","Other","Retouch","Delivery"];
        string[] names=["Morgan","Taylor","Alex","Jordan","Casey","Riley"];
        for(int workspace=1;workspace<=3;workspace++)
        {
            Execute(connection,transaction,"insert into studio_workspace_announcements(id,workspace_id,title,body,created_by) values($1,$2,$3,$4,$5)",Id(20,workspace),Id(1,workspace),"DEMO — Explore this workspace","Open a job to try Gallery collections and version history, Subjects, Tasks / Calendar and Activity. All sample images are synthetic illustrations; nothing is published.",login);
            Execute(connection,transaction,"insert into studio_schedule_resources(id,name,resource_type,is_active,notes,created_at_utc) values($1,$2,'Room',true,'Fictional demo resource',now())",Id(21,workspace),$"DEMO — Studio {workspace}");
        }
        for(int index=0;index<JobNumbers.Length;index++)
        {
            int number=JobNumbers[index],workspace=number<=2?1:number<=5?2:3;
            Guid job=Id(3,number);
            for(int task=0;task<4;task++)
            {
                int key=number*10+task;
                DateTimeOffset start=today.AddDays(index==0 ? task-1 : index*2+task).AddHours(14);
                string status=index==0&&task==0?"Completed":index==0&&task==1?"InProgress":task==2&&index==3?"Blocked":"NotStarted";
                Execute(connection,transaction,"insert into studio_job_activities(id,job_id,activity_type,title,description,status,priority,assigned_to,scheduled_at_utc,due_at_utc,started_at_utc,completed_at_utc,created_by,created_at_utc,updated_at_utc) values($1,$2,$3,$4,'Fictional demo task',$5,'Normal',$6,$7,$8,case when $5 in ('Completed','InProgress') then $7 else null end,case when $5='Completed' then $8 else null end,$6,now(),now())",Id(22,key),job,types[task],"DEMO — "+phases[task],status,login,start,start.AddHours(2));
                Execute(connection,transaction,"insert into studio_operational_audit(id,job_id,activity_id,action,user_name,detail,created_at_utc) values($1,$2,$3,'DemoSeed',$4,$5,now())",Id(23,key),job,Id(22,key),login,"Sample task added: "+phases[task]);
            }
            for(int slot=0;slot<2;slot++)
            {
                int key=number*10+slot;
                DateTimeOffset start=today.AddDays(index*2+slot).AddHours(15);
                Execute(connection,transaction,"insert into studio_schedule_entries(id,job_id,title,starts_at_utc,ends_at_utc,location,status,notes,created_at_utc) values($1,$2,$3,$4,$5,$6,'Scheduled','Fictional demo appointment',now())",Id(24,key),job,slot==0?"DEMO — Photo session":"DEMO — Client review",start,start.AddHours(1),$"DEMO Studio {workspace}");
                Execute(connection,transaction,"insert into studio_schedule_entry_resources(entry_id,resource_id) values($1,$2)",Id(24,key),Id(21,workspace));
            }
            int subjects=index>=4?6:1;
            for(int subject=0;subject<subjects;subject++)
                Execute(connection,transaction,"insert into studio_job_subjects(id,job_id,name,reference,notes,updated_by) values($1,$2,$3,$4,'Fictional demo subject',$5)",Id(25,number*10+subject),job,names[(index+subject)%names.Length],$"DEMO-{number}-{subject+1:00}",login);
            for(int asset=0;asset<4;asset++)
            {
                string key=$"demo-{number}-{asset}";
                Guid original=Id(26,number*10+asset);
                AddImage(connection,transaction,root,job,key,original,1,index*4+asset,null,login);
                if(asset==0)AddImage(connection,transaction,root,job,key,Id(27,number),2,index*4+asset+1,original,login);
                Execute(connection,transaction,"insert into studio_job_asset_collections(job_id,asset_key,collection_name) values($1,$2,$3)",job,key,asset<2?"Demo selects":"Demo proofs");
            }
            Execute(connection,transaction,"insert into studio_messages(id,conversation_id,author_display_name,visibility,body,created_at_utc) values($1,$2,'Studio','Internal',$3,now())",Id(28,number),Id(2,number),"Demo gallery ready: four sample illustrations across Selects and Proofs, with two versions of the first image. Explore the scheduled tasks and subject records below.");
        }
        Execute(connection,transaction,"insert into studio_state(key,value,updated_at_utc) values('demo-data-v2',$1,now())",login);
        return true;
    }

    private static void AddImage(NpgsqlConnection connection,NpgsqlTransaction transaction,string root,Guid job,string key,Guid id,int version,int variant,Guid? source,string login)
    {
        string relative=Path.Combine(job.ToString("N"),"demo",id.ToString("N")+".png");
        string full=Path.Combine(root,relative);Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        // Stable paths let a retry reuse files left by a rolled-back database transaction.
        if(!File.Exists(full))
        {
            using Image<Rgb24> image=CreateIllustration(variant);
            string temporary=full+"."+Guid.NewGuid().ToString("N")+".tmp";
            try {image.SaveAsPng(temporary);File.Move(temporary,full,false);}
            finally {if(File.Exists(temporary))File.Delete(temporary);}
        }
        using var stream=File.OpenRead(full);
        string hash=Convert.ToHexString(SHA256.HashData(stream));
        Execute(connection,transaction,"insert into studio_asset_versions(id,job_id,asset_key,version_number,file_name,content_type,size_bytes,sha256,stored_path,created_by,created_at_utc,source_version_id,note) values($1,$2,$3,$4,$5,'image/png',$6,$7,$8,$9,now(),$10,$11)",id,job,key,version,$"Demo-landscape-{variant+1:00}-v{version}.png",stream.Length,hash,relative,login,(object?)source??DBNull.Value,"Synthetic demo illustration generated by Studio; not a client photograph. "+(version==1?"Original demo version.":"Alternate synthetic proof linked to the original demo version."));
    }

    internal static Image<Rgb24> CreateIllustration(int variant)
    {
        var image=new Image<Rgb24>(640,420);
        for(int y=0;y<420;y++)for(int x=0;x<640;x++)
        {
            double t=y/420d;int warmth=variant%6*8;
            Rgb24 colour=new((byte)(210-t*70),(byte)(190-t*55),(byte)(160+warmth-t*35));
            int sunX=100+variant%4*125;
            if((x-sunX)*(x-sunX)+(y-105)*(y-105)<42*42)colour=new(255,236,178);
            double ridge=220+45*Math.Sin(x/100d+variant)+25*Math.Cos(x/43d);
            if(y>ridge)colour=new((byte)(75+warmth),(byte)(103+warmth/2),(byte)(91+warmth/3));
            if(y>305+20*Math.Sin(x/80d))colour=new(51,76,65);
            if(y>395)colour=new((byte)(130+warmth),107,77);
            image[x,y]=colour;
        }
        return image;
    }
    private static void Execute(NpgsqlConnection connection,NpgsqlTransaction transaction,string sql,params object[] values)
    {
        using var command=new NpgsqlCommand(sql,connection,transaction);
        foreach(object value in values)command.Parameters.AddWithValue(value);
        command.ExecuteNonQuery();
    }
}

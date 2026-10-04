using System.Security.Cryptography;
using System.Net.Http;
using Npgsql;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using Common.Storage;

namespace Aegis.Studio.Web.Operations;

// Only deterministic DEMO identifiers are used; no real jobs or files are changed.
internal static class DemoDataExpansion
{
    private static readonly HttpClient demoPhotoClient = CreateDemoPhotoClient();

    private static HttpClient CreateDemoPhotoClient()
    {
        var client=new HttpClient { Timeout=TimeSpan.FromSeconds(30) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Aegis.Studio/1.0 (demo-data)");
        client.DefaultRequestHeaders.Accept.ParseAdd("image/avif,image/webp,image/apng,image/svg+xml,image/*,*/*;q=0.8");
        return client;
    }

    private static readonly string[] weddingPhotoUrls =
    [
        "https://commons.wikimedia.org/wiki/Special:Redirect/file/Bride_and_bridegroom.jpg",
        "https://commons.wikimedia.org/wiki/Special:Redirect/file/Bride_and_Groom_at_their_introduction_ceremony.1.jpg",
        "https://commons.wikimedia.org/wiki/Special:Redirect/file/Wedding_couple_(1294168).jpg",
        "https://commons.wikimedia.org/wiki/Special:Redirect/file/Wedding_couple_(1293933).jpg",
        "https://commons.wikimedia.org/wiki/Special:Redirect/file/Wedding_couple_(1294004).jpg"
    ];
    private static readonly Dictionary<string,byte[]> weddingPhotoCache = new(StringComparer.OrdinalIgnoreCase);
    internal static readonly int[] JobNumbers = [1,2,4,5,7,8];
    internal static Guid Id(int group,int number) => Guid.Parse($"de000000-0000-4000-{group:0000}-{number:000000000000}");

    internal static bool Seed(NpgsqlConnection connection,NpgsqlTransaction transaction,string login,IFileStorage storage)
    {
        var versionStorage=new PrefixedFileStorage(storage,"VersionStore");
        DateTimeOffset today=new(DateTime.UtcNow.Date,TimeSpan.Zero);
        string[] phases=["Prepare equipment","Capture portraits","Retouch selected images","Review and delivery"];
        string[] types=["EquipmentPrep","Other","Retouch","Delivery"];
        string[] names=["Morgan","Taylor","Alex","Jordan","Casey","Riley"];
        for(int workspace=1;workspace<=3;workspace++)
        {
            Execute(connection,transaction,"insert into studio_workspace_announcements(id,workspace_id,title,body,created_by) values($1,$2,$3,$4,$5)",Id(20,workspace),Id(1,workspace),"DEMO — Explore this workspace","Open a job to try Media collections and version history, People, Tasks / Calendar and Activity. Carter Wedding includes fifty seeded wedding photographs with people assignments; nothing is published.",login);
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
            Guid[] weddingSubjects=[];
            if(number==4)
            {
                string[] weddingNames=[
                    "Emily Carter",
                    "Daniel Carter",
                    "Emily & Daniel",
                    "Bridal Party",
                    "Groomsmen",
                    "Carter Family",
                    "Parents of the Bride",
                    "Parents of the Groom",
                    "Guests"
                ];
                string[] weddingRoles=[
                    "Bride",
                    "Groom",
                    "Couple",
                    "Wedding party",
                    "Wedding party",
                    "Family group",
                    "Family group",
                    "Family group",
                    "Reception guests"
                ];
                weddingSubjects=new Guid[weddingNames.Length];
                for(int subject=0;subject<weddingNames.Length;subject++)
                {
                    Guid subjectId=Id(25,number*10+subject);
                    weddingSubjects[subject]=subjectId;
                    Execute(connection,transaction,
                        "insert into studio_job_subjects(id,job_id,name,reference,notes,updated_by) values($1,$2,$3,$4,$5,$6) on conflict(id) do update set name=excluded.name,reference=excluded.reference,notes=excluded.notes,updated_by=excluded.updated_by",
                        subjectId,job,weddingNames[subject],$"WED-{subject+1:00}",weddingRoles[subject],login);
                }
            }
            else
            {
                int subjects=index>=4?6:1;
                for(int subject=0;subject<subjects;subject++)
                    Execute(connection,transaction,"insert into studio_job_subjects(id,job_id,name,reference,notes,updated_by) values($1,$2,$3,$4,'Fictional demo subject',$5)",Id(25,number*10+subject),job,names[(index+subject)%names.Length],$"DEMO-{number}-{subject+1:00}",login);
            }

            int assetCount=number==4?50:4;
            for(int asset=0;asset<assetCount;asset++)
            {
                string key=$"demo-{number}-{asset}";
                Guid requestedOriginal=Id(26,number*100+asset);
                Guid original=number==4
                    ? AddWeddingPhoto(connection,transaction,versionStorage,job,key,requestedOriginal,1,asset,null,login)
                    : AddImage(connection,transaction,versionStorage,job,key,requestedOriginal,1,index*16+asset,null,login);
                if(asset==0)
                {
                    if(number==4) AddWeddingPhoto(connection,transaction,versionStorage,job,key,Id(27,number),2,asset+1,original,login);
                    else AddImage(connection,transaction,versionStorage,job,key,Id(27,number),2,index*16+asset+1,original,login);
                }
                string collection=number==4
                    ? asset<8?"Getting Ready":asset<22?"Ceremony":asset<38?"Portraits":"Reception"
                    : asset<2?"Demo selects":"Demo proofs";
                Execute(connection,transaction,"insert into studio_job_asset_collections(job_id,asset_key,collection_name) values($1,$2,$3)",job,key,collection);
                Execute(connection,transaction,"update studio_job_asset_collections set collection_name=$3 where job_id=$1 and asset_key=$2",job,key,collection);

                if(number==4)
                {
                    Guid requestedMediaId=Id(29,number*100+asset);
                    Execute(connection,transaction,
                        "insert into studio_media_galleries(id) values($1) on conflict(id) do nothing",
                        job);
                    Guid mediaId=ExecuteGuid(connection,transaction,
                        "insert into studio_media_assets(id,job_id,gallery_id,asset_key,production_status) values($1,$2,$2,$3,$4) on conflict(job_id,asset_key) do update set production_status=excluded.production_status returning id",
                        requestedMediaId,job,key,asset<34?"Photographed":"Needs Review");

                    int[][] assignments=[
                        [0,2], [1,2], [0], [1], [0,3], [1,4], [2], [0,2],
                        [0,1,2], [0,1,2], [3], [4], [0,1], [5], [6], [7],
                        [0,1,3], [0,1,4], [2,5], [2,6], [2,7], [0,1,2],
                        [0,2], [1,2], [0], [1], [2], [3], [4], [5],
                        [6], [7], [0,1], [0,2], [1,2], [0,1,2], [3,4], [5],
                        [8], [8], [0,1,2], [3], [4], [5], [6], [7], [8], [0,1], [2], [0,1,2]
                    ];
                    foreach(int subjectIndex in assignments[asset])
                        Execute(connection,transaction,
                            "insert into studio_media_asset_subjects(asset_id,subject_id) values($1,$2) on conflict do nothing",
                            mediaId,weddingSubjects[subjectIndex]);
                }
            }
            string mediaMessage=number==4
                ?"Demo gallery ready: fifty wedding photographs across Getting Ready, Ceremony, Portraits and Reception, linked to the people and groups in the People roster."
                :"Demo gallery ready: four sample illustrations across Selects and Proofs, with two versions of the first image.";
            Execute(connection,transaction,"insert into studio_messages(id,conversation_id,author_display_name,visibility,body,created_at_utc) values($1,$2,'Studio','Internal',$3,now())",Id(28,number),Id(2,number),mediaMessage);
        }
        Execute(connection,transaction,"insert into studio_state(key,value,updated_at_utc) values('demo-data-v3',$1,now()) on conflict(key) do update set value=excluded.value,updated_at_utc=excluded.updated_at_utc",login);
        return true;
    }

    private static Guid AddWeddingPhoto(NpgsqlConnection connection,NpgsqlTransaction transaction,IFileStorage storage,Guid job,string key,Guid id,int version,int photoIndex,Guid? source,string login)
    {
        byte[]? bytes=null;
        string? selectedUrl=null;
        Exception? lastError=null;
        for(int attempt=0;attempt<weddingPhotoUrls.Length;attempt++)
        {
            string url=weddingPhotoUrls[(photoIndex+attempt)%weddingPhotoUrls.Length];
            try
            {
                if(!weddingPhotoCache.TryGetValue(url,out bytes))
                {
                    using HttpResponseMessage response=demoPhotoClient.GetAsync(url).GetAwaiter().GetResult();
                    response.EnsureSuccessStatusCode();
                    bytes=response.Content.ReadAsByteArrayAsync().GetAwaiter().GetResult();
                    weddingPhotoCache[url]=bytes;
                }
                selectedUrl=url;
                break;
            }
            catch(Exception error)
            {
                lastError=error;
                bytes=null;
            }
        }
        if(bytes is null)
            throw new InvalidOperationException("Unable to download any configured CC0 wedding demo photograph.",lastError);

        using var content=new MemoryStream(bytes,false);
        string stored=$"{job:N}/demo/{id:N}.jpg";
        string fileName=$"Carter-Wedding-{photoIndex+1:00}-v{version}.jpg";
        StoredFile storedFile=storage.StoreAsync(new StorageWriteRequest(
            stored,
            content,
            "image/jpeg",
            fileName,
            login,
            new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase)
            {
                ["Application"]="Aegis.Studio",
                ["JobId"]=job.ToString("D"),
                ["AssetKey"]=key,
                ["VersionId"]=id.ToString("D"),
                ["Demo"]="true",
                ["DemoSource"]="Wikimedia Commons CC0",
                ["DemoSourceUrl"]=selectedUrl??string.Empty
            })).GetAwaiter().GetResult();

        const string sql="""
            insert into studio_asset_versions
            (id,job_id,asset_key,version_number,file_name,content_type,size_bytes,sha256,stored_path,created_by,created_at_utc,source_version_id,note)
            values($1,$2,$3,$4,$5,'image/jpeg',$6,$7,$8,$9,now(),$10,$11)
            on conflict(job_id,asset_key,version_number) do update set
              file_name=excluded.file_name,
              content_type=excluded.content_type,
              size_bytes=excluded.size_bytes,
              sha256=excluded.sha256,
              stored_path=excluded.stored_path,
              created_by=excluded.created_by,
              source_version_id=excluded.source_version_id,
              note=excluded.note
            returning id
            """;

        using var command=new NpgsqlCommand(sql,connection,transaction);
        object[] values=[
            id,job,key,version,fileName,storedFile.Length,storedFile.Sha256,stored,login,
            (object?)source??DBNull.Value,
            "CC0 wedding photograph from Wikimedia Commons, seeded for Studio demonstration data."
        ];
        foreach(object value in values)command.Parameters.AddWithValue(value);
        return (Guid)(command.ExecuteScalar()??throw new InvalidOperationException("Demo wedding media upsert did not return an id."));
    }

    private static Guid AddImage(NpgsqlConnection connection,NpgsqlTransaction transaction,IFileStorage storage,Guid job,string key,Guid id,int version,int variant,Guid? source,string login)
    {
        using Image<Rgb24> image=CreateIllustration(variant);
        using var content=new MemoryStream();
        image.SaveAsPng(content);
        content.Position=0;

        string stored=$"{job:N}/demo/{id:N}.png";
        string fileName=$"Demo-landscape-{variant+1:00}-v{version}.png";
        StoredFile storedFile=storage.StoreAsync(new StorageWriteRequest(
            stored,
            content,
            "image/png",
            fileName,
            login,
            new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase)
            {
                ["Application"]="Aegis.Studio",
                ["JobId"]=job.ToString("D"),
                ["AssetKey"]=key,
                ["VersionId"]=id.ToString("D"),
                ["Demo"]="true"
            })).GetAwaiter().GetResult();

        const string sql="""
            insert into studio_asset_versions
            (id,job_id,asset_key,version_number,file_name,content_type,size_bytes,sha256,stored_path,created_by,created_at_utc,source_version_id,note)
            values($1,$2,$3,$4,$5,'image/png',$6,$7,$8,$9,now(),$10,$11)
            on conflict(job_id,asset_key,version_number) do update set
              file_name=excluded.file_name,
              content_type=excluded.content_type,
              size_bytes=excluded.size_bytes,
              sha256=excluded.sha256,
              stored_path=excluded.stored_path,
              created_by=excluded.created_by,
              source_version_id=excluded.source_version_id,
              note=excluded.note
            returning id
            """;

        using var command=new NpgsqlCommand(sql,connection,transaction);
        object[] values=[
            id,job,key,version,fileName,storedFile.Length,storedFile.Sha256,stored,login,
            (object?)source??DBNull.Value,
            "Synthetic demo illustration generated by Studio; not a client photograph. "+(version==1?"Original demo version.":"Alternate synthetic proof linked to the original demo version.")
        ];
        foreach(object value in values)command.Parameters.AddWithValue(value);
        return (Guid)(command.ExecuteScalar()??throw new InvalidOperationException("Demo media version upsert did not return an id."));
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
    private static Guid ExecuteGuid(NpgsqlConnection connection,NpgsqlTransaction transaction,string sql,params object[] values)
    {
        using var command=new NpgsqlCommand(sql,connection,transaction);
        foreach(object value in values)command.Parameters.AddWithValue(value);
        object? result=command.ExecuteScalar();
        if(result is Guid id)return id;
        throw new InvalidOperationException("Expected a Guid result from demo data command.");
    }

    private static void Execute(NpgsqlConnection connection,NpgsqlTransaction transaction,string sql,params object[] values)
    {
        string commandText=sql.Trim();
        if(commandText.StartsWith("insert into ",StringComparison.OrdinalIgnoreCase)
            && !commandText.Contains("on conflict",StringComparison.OrdinalIgnoreCase))
            commandText=commandText.TrimEnd(';')+" on conflict do nothing;";
        using var command=new NpgsqlCommand(commandText,connection,transaction);
        foreach(object value in values)command.Parameters.AddWithValue(value);
        command.ExecuteNonQuery();
    }
}

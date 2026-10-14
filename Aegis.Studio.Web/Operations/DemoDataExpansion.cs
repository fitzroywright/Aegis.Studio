using System.Security.Cryptography;
using Aegis.Studio.Web.Media;
using System.Net.Http;
using Npgsql;
using Common.Storage;

namespace Aegis.Studio.Web.Operations;

// Only deterministic DEMO identifiers are used; no real jobs or files are changed.
internal static partial class DemoDataExpansion
{
    private static readonly HttpClient demoPhotoClient = CreateDemoPhotoClient();

    private static HttpClient CreateDemoPhotoClient()
    {
        var client=new HttpClient { Timeout=TimeSpan.FromSeconds(30) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Aegis.Studio/1.0 (demo-data)");
        client.DefaultRequestHeaders.Accept.ParseAdd("image/avif,image/webp,image/apng,image/svg+xml,image/*,*/*;q=0.8");
        return client;
    }

    private sealed record DemoPhotoCandidate(string Url,string Source,string License,string Page);

    private static HttpResponseMessage DownloadDemoPhoto(string preferredUrl,int variant,string? searchTerm=null)
    {
        var cataloguePhoto=DemoPortraitCatalogue.Photos.FirstOrDefault(photo=>photo.Url==preferredUrl);
        // Never replace category-specific demo photographs with an unrelated portrait.
        // A failed category download must be reported rather than silently mislabelled.
        bool categorySpecific=searchTerm is not null &&
            new[]{"Wedding","Engagement","Graduation","School","Product","Commercial","Family","Newborn","Sports","Athlete","Team"}
                .Any(term=>searchTerm.Contains(term,StringComparison.OrdinalIgnoreCase));
        DemoPhotoCandidate[] candidates=categorySpecific
            ? [new(preferredUrl,"Wikimedia Commons",cataloguePhoto?.License??"Catalogue source",cataloguePhoto?.Page??preferredUrl)]
            : [
                new(preferredUrl,"Wikimedia Commons",cataloguePhoto?.License??"Catalogue source",cataloguePhoto?.Page??preferredUrl),
                new(DemoPortraitCatalogue.Url(Math.Abs(variant)%DemoPortraitCatalogue.Photos.Count),"Wikimedia Commons","Catalogue licence",DemoPortraitCatalogue.Photos[Math.Abs(variant)%DemoPortraitCatalogue.Photos.Count].Page)
            ];

        int start=0;
        Exception? lastError=null;
        for(int offset=0;offset<candidates.Length;offset++)
        {
            DemoPhotoCandidate candidate=candidates[(start+offset)%candidates.Length];
            for(int retry=0;retry<3;retry++)
            {
                try
                {
                    HttpResponseMessage response=demoPhotoClient.GetAsync(candidate.Url).GetAwaiter().GetResult();
                    if(response.IsSuccessStatusCode)
                    {
                        response.Headers.TryAddWithoutValidation("X-Aegis-Demo-Source",candidate.Source);
                        response.Headers.TryAddWithoutValidation("X-Aegis-Demo-License",candidate.License);
                        response.Headers.TryAddWithoutValidation("X-Aegis-Demo-Page",candidate.Page);
                        return response;
                    }
                    int status=(int)response.StatusCode;
                    lastError=new HttpRequestException($"Demo image source {candidate.Source} returned HTTP {status}.");
                    if(status!=429&&status<500)
                    {
                        response.Dispose();
                        break;
                    }
                    TimeSpan wait=response.Headers.RetryAfter?.Delta??TimeSpan.FromSeconds(Math.Min(8,1*Math.Pow(2,retry)));
                    response.Dispose();
                    Task.Delay(TimeSpan.FromSeconds(Math.Clamp(wait.TotalSeconds,1,10))).GetAwaiter().GetResult();
                }
                catch(Exception error)
                {
                    lastError=error;
                    break;
                }
            }
        }
        throw new HttpRequestException("All configured demo image sources failed.",lastError);
    }

    private static string Header(HttpResponseMessage response,string name,string fallback)
        => response.Headers.TryGetValues(name,out IEnumerable<string>? values)?values.FirstOrDefault()??fallback:fallback;

    private static readonly string[] weddingPhotoUrls =
    [
        "https://commons.wikimedia.org/wiki/Special:Redirect/file/Bride_and_bridegroom.jpg",
        "https://commons.wikimedia.org/wiki/Special:Redirect/file/Bride_and_Groom_at_their_introduction_ceremony.1.jpg",
        "https://commons.wikimedia.org/wiki/Special:Redirect/file/Wedding_couple_(1294168).jpg",
        "https://commons.wikimedia.org/wiki/Special:Redirect/file/Wedding_couple_(1293933).jpg",
        "https://commons.wikimedia.org/wiki/Special:Redirect/file/Wedding_couple_(1294004).jpg"
    ];
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string,byte[]> weddingPhotoCache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string,string> demoPhotoSourceCache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string,string> demoPhotoSourceNameCache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string,string> demoPhotoLicenseCache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string,string> demoPhotoPageCache = new(StringComparer.OrdinalIgnoreCase);
    // Curated real photographs only. Each file is CC0 or public domain on Wikimedia Commons.
    // Keep the Commons file URL in storage metadata so every seeded image retains provenance.
    private static readonly string[] realPhotoUrls =
    [
        "https://commons.wikimedia.org/wiki/Special:Redirect/file/Graduating_pupils.jpg",
        "https://commons.wikimedia.org/wiki/Special:Redirect/file/Graduating_Students(GN11751).jpg",
        "https://commons.wikimedia.org/wiki/Special:Redirect/file/Graduation_pictures.jpg",
        "https://commons.wikimedia.org/wiki/Special:Redirect/file/Ceremony_graduation.jpg",
        "https://commons.wikimedia.org/wiki/Special:Redirect/file/Headshot.Corporate.2023.jpg",
        "https://commons.wikimedia.org/wiki/Special:Redirect/file/Woman_(headshot).jpg",
        "https://commons.wikimedia.org/wiki/Special:Redirect/file/Parents_and_their_baby.jpg",
        "https://commons.wikimedia.org/wiki/Special:Redirect/file/New_born_baby_with_father.jpg",
        "https://commons.wikimedia.org/wiki/Special:Redirect/file/Team_picture_time.jpg",
        "https://commons.wikimedia.org/wiki/Special:Redirect/file/Woman_(headshot).jpg",
        "https://commons.wikimedia.org/wiki/Special:Redirect/file/People_at_Wedding_ceremony.jpg",
        "https://commons.wikimedia.org/wiki/Special:Redirect/file/A_group_picture_together_with_the_groom_at_Wedding_ceremony.jpg",
        "https://commons.wikimedia.org/wiki/Special:Redirect/file/Family_portrait.jpg"
    ];
    internal static readonly int[] JobNumbers = [1,2,4,5,7,8];
    internal static Guid Id(int group,int number) => Guid.Parse($"de000000-0000-4000-{group:0000}-{number:000000000000}");

    internal static bool Seed(NpgsqlConnection connection,NpgsqlTransaction transaction,string login,IFileStorage storage,Action<DemoSeedProgress>? report=null)
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
            report?.Invoke(new(10 + 20 * index / JobNumbers.Length, "Legacy demo jobs", index, JobNumbers.Length, "Preparing schedules, subjects and real photographs."));
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

            int assetCount=number==4?5:4;
            for(int asset=0;asset<assetCount;asset++)
            {
                report?.Invoke(new(10 + (int)(20 * (index + (double)asset / assetCount) / JobNumbers.Length), "Legacy photographs", asset, assetCount, $"Job {index+1}/{JobNumbers.Length}: downloading and storing photo {asset+1}/{assetCount}."));
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
                ?"Demo gallery ready: five distinct wedding photographs across Getting Ready, Ceremony, Portraits and Reception, linked to the people and groups in the People roster."
                :"Demo gallery ready: four real public-domain photographs across Selects and Proofs, with two versions of the first image.";
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
                bytes=PhotoBytes(url,photoIndex,"wedding");
                selectedUrl=demoPhotoSourceCache.GetValueOrDefault(url,url);
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
                ["DemoSource"]=demoPhotoSourceNameCache.FirstOrDefault(x=>x.Value==selectedUrl).Value??"Demo image source",
                ["DemoSourceUrl"]=selectedUrl??string.Empty,
                ["DemoSourcePage"]=demoPhotoPageCache.FirstOrDefault(x=>demoPhotoSourceCache.GetValueOrDefault(x.Key)==selectedUrl).Value??selectedUrl??string.Empty,
                ["DemoSourceLicense"]=demoPhotoLicenseCache.FirstOrDefault(x=>demoPhotoSourceCache.GetValueOrDefault(x.Key)==selectedUrl).Value??"Provider terms apply"
            })).GetAwaiter().GetResult();

        using(var verified=storage.OpenReadAsync(stored).GetAwaiter().GetResult())
            if(!Convert.ToHexString(SHA256.HashData(verified)).Equals(storedFile.Sha256,StringComparison.OrdinalIgnoreCase))
                throw new StorageException("STORAGE-INTEGRITY-001","Seeded photo verification failed.");
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
            $"Demo photograph from {demoPhotoSourceNameCache.FirstOrDefault(x=>x.Value==selectedUrl).Value??"configured source"}, seeded for Studio demonstration data."
        ];
        foreach(object value in values)command.Parameters.AddWithValue(value);
        Guid result=(Guid)(command.ExecuteScalar()??throw new InvalidOperationException("Demo wedding media upsert did not return an id."));
        return result;
    }

    internal static Guid AddImage(NpgsqlConnection connection,NpgsqlTransaction transaction,IFileStorage storage,Guid job,string key,Guid id,int version,int variant,Guid? source,string login,string? sourceUrl=null,string? searchTerm=null)
    {
        byte[]? bytes=null;
        string? selectedUrl=null;
        Exception? lastError=null;
        for(int attempt=0;bytes is null&&attempt<(sourceUrl is null?realPhotoUrls.Length:1);attempt++)
        {
            string url=sourceUrl??realPhotoUrls[(variant+attempt)%realPhotoUrls.Length];
            try
            {
                bytes=PhotoBytes(url,variant,searchTerm);
                selectedUrl=demoPhotoSourceCache.GetValueOrDefault(url,url);
                break;
            }
            catch(Exception error)
            {
                lastError=error;
                bytes=null;
            }
        }
        if(bytes is null)
            throw new InvalidOperationException("Unable to download a configured demo photograph.",lastError);

        string extension=".jpg";
        string contentType="image/jpeg";
        using var content=new MemoryStream(bytes,false);
        string stored=$"{job:N}/demo/{id:N}{extension}";
        string fileName=$"Demo-photo-{variant+1:000}-v{version}{extension}";
        StoredFile storedFile=storage.StoreAsync(new StorageWriteRequest(
            stored,
            content,
            contentType,
            fileName,
            login,
            new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase)
            {
                ["Application"]="Aegis.Studio",
                ["JobId"]=job.ToString("D"),
                ["AssetKey"]=key,
                ["VersionId"]=id.ToString("D"),
                ["Demo"]="true",
                ["DemoSource"]=sourceUrl is not null?demoPhotoSourceNameCache.GetValueOrDefault(sourceUrl,"Wikimedia Commons"):demoPhotoSourceNameCache.FirstOrDefault(x=>x.Value==selectedUrl).Value??"Demo image source",
                ["DemoSourceUrl"]=selectedUrl??string.Empty,
                ["DemoSourcePage"]=sourceUrl is not null?demoPhotoPageCache.GetValueOrDefault(sourceUrl,selectedUrl??string.Empty):selectedUrl??string.Empty,
                ["DemoSourceLicense"]=sourceUrl is not null?demoPhotoLicenseCache.GetValueOrDefault(sourceUrl,"Provider terms apply"):"Provider terms apply"
            })).GetAwaiter().GetResult();

        using(var verified=storage.OpenReadAsync(stored).GetAwaiter().GetResult())
            if(!Convert.ToHexString(SHA256.HashData(verified)).Equals(storedFile.Sha256,StringComparison.OrdinalIgnoreCase))
                throw new StorageException("STORAGE-INTEGRITY-001","Seeded photo verification failed.");
        const string sql="""
            insert into studio_asset_versions
            (id,job_id,asset_key,version_number,file_name,content_type,size_bytes,sha256,stored_path,created_by,created_at_utc,source_version_id,note,demo_source)
            values($1,$2,$3,$4,$5,$6,$7,$8,$9,$10,now(),$11,$12,$13::jsonb)
            on conflict(job_id,asset_key,version_number) do update set
              file_name=excluded.file_name,
              content_type=excluded.content_type,
              size_bytes=excluded.size_bytes,
              sha256=excluded.sha256,
              stored_path=excluded.stored_path,
              created_by=excluded.created_by,
              source_version_id=excluded.source_version_id,
              note=excluded.note,
              demo_source=excluded.demo_source
            returning id
            """;
        using var command=new NpgsqlCommand(sql,connection,transaction);
        object[] values=[
            id,job,key,version,fileName,contentType,storedFile.Length,storedFile.Sha256,stored,login,
            (object?)source??DBNull.Value,
            $"Demo photograph from {(sourceUrl is not null?demoPhotoSourceNameCache.GetValueOrDefault(sourceUrl,"configured source"):"configured source")}. Source URL retained in Common.Storage metadata.",
            System.Text.Json.JsonSerializer.Serialize(new { demo=true, source=selectedUrl, license=sourceUrl is not null?demoPhotoLicenseCache.GetValueOrDefault(sourceUrl,"Catalogue photograph"):"Catalogue photograph" })
        ];
        foreach(object value in values)command.Parameters.AddWithValue(value);
        Guid result=(Guid)(command.ExecuteScalar()??throw new InvalidOperationException("Demo photograph upsert did not return an id."));
        return result;
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



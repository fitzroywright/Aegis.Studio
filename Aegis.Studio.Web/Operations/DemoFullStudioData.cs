using System.Text.Json;
using Npgsql;
using Common.Storage;

namespace Aegis.Studio.Web.Operations;

// Broad, deterministic demo dataset used by the click-through Studio demonstration.
// It only owns IDs in the de000000 demo namespace and never deletes real records.
internal static class DemoFullStudioData
{
    private sealed record DemoWorkspace(int Number,string Name);
    private sealed record DemoClient(int Number,string Name,string Email,string Phone);
    private sealed record DemoJob(int Number,int Workspace,int Client,string Title,string Status,int DayOffset,string Location,decimal Price,decimal Paid,string Photographer,string Type);

    private static readonly DemoWorkspace[] Workspaces =
    [
        new(1,"Portraits and IDs"),
        new(2,"Weddings and Events"),
        new(3,"Schools and Corporate"),
        new(4,"Graduations"),
        new(5,"Commercial and Products"),
        new(6,"Sports and Groups")
    ];

    private static readonly DemoClient[] Clients =
    [
        new(1,"Miller Family","sarah.miller@example.invalid","876-555-0101"),
        new(2,"Campion College","office@campion-demo.example.invalid","876-555-0102"),
        new(3,"Carter Corporation","admin@cartercorp-demo.example.invalid","876-555-0103"),
        new(4,"Emily & Daniel Carter","emily.carter@example.invalid","876-555-0104"),
        new(5,"Harrison Family","harrison@example.invalid","876-555-0105"),
        new(6,"Peterson Family","peterson@example.invalid","876-555-0106"),
        new(7,"Wilson Family","wilson@example.invalid","876-555-0107"),
        new(8,"Thompson Family","thompson@example.invalid","876-555-0108"),
        new(9,"North Coast Academy","office@nca-demo.example.invalid","876-555-0109"),
        new(10,"Kingston Medical Group","hr@kmg-demo.example.invalid","876-555-0110"),
        new(11,"Harbour Conference Ltd","events@harbour-demo.example.invalid","876-555-0111"),
        new(12,"Blue Mountain Products","marketing@bmp-demo.example.invalid","876-555-0112")
    ];

    private static readonly DemoJob[] Jobs =
    [
        new(101,1,1,"Miller Family - Fall Portraits","Production",0,"Studio A",45000,22500,"demo.sam","Family Portrait"),
        new(102,1,5,"Harrison Senior Session","Scheduled",0,"Studio B",38000,38000,"demo.jason","Senior Portrait"),
        new(103,1,6,"Peterson Newborn","Scheduled",0,"Client Home",52000,26000,"demo.anna","Newborn"),
        new(104,1,7,"Wilson Family + Dog","Scheduled",0,"Hope Gardens",42000,42000,"demo.sam","Family + Pets"),
        new(105,1,8,"Thompson Passport & Visa Photos","Completed",-7,"Front Desk Studio",12000,12000,"demo.michael","Passport / Visa"),
        new(106,1,3,"Carter Corp Employee IDs","Scheduled",2,"Carter Corporation HQ",96000,48000,"demo.jason","Employee ID"),

        new(201,2,4,"Carter Wedding","Production",0,"Hope Gardens",320000,240000,"demo.sam","Wedding"),
        new(202,2,4,"Carter Engagement Session","Completed",-30,"Devon House",65000,65000,"demo.sam","Engagement"),
        new(203,2,11,"Harbour Leadership Conference","QC",1,"Jamaica Pegasus",180000,90000,"demo.michael","Corporate Event"),
        new(204,2,7,"Wilson Anniversary Event","Awaiting Upload",-1,"Terra Nova",95000,50000,"demo.anna","Event"),

        new(301,3,3,"Carter Executive Headshots","Editing",0,"Studio B",84000,84000,"demo.jason","Executive Headshots"),
        new(302,3,10,"KMG Staff Headshot Day","Scheduled",3,"Kingston Medical Group",165000,82500,"demo.michael","Corporate Headshots"),
        new(303,3,9,"North Coast Academy School Portraits","Culling",-3,"North Coast Academy",240000,120000,"demo.anna","School Portraits"),
        new(304,3,2,"Campion Class Group Photos","Client Review",-5,"Campion College",195000,195000,"demo.jason","Class / Group"),

        new(401,4,2,"Campion Graduation 2026","Printing",0,"Campion College",275000,137500,"demo.sam","Graduation"),
        new(402,4,9,"North Coast Academy Graduation","Scheduled",4,"North Coast Academy",210000,105000,"demo.anna","Graduation"),
        new(403,4,2,"Campion Graduation Retakes","Needs Scheduling",8,"Studio A",45000,0,"demo.jason","Graduation Retakes"),
        new(404,4,9,"NCA Valedictorian Portraits","Ready for Delivery",-2,"Studio B",56000,56000,"demo.michael","Graduation Portrait"),

        new(501,5,12,"Blue Mountain Product Catalogue","Retouching",-1,"Commercial Bay",225000,112500,"demo.michael","Product Photography"),
        new(502,5,3,"Carter Annual Report Portraits","QC",-4,"Carter Corporation HQ",125000,125000,"demo.sam","Corporate Portrait"),
        new(503,5,12,"Blue Mountain Holiday Campaign","Booking",12,"Location TBD",185000,0,"demo.anna","Commercial Campaign"),

        new(601,6,8,"Thompson Youth Football Team","Printing",-2,"National Stadium",135000,90000,"demo.jason","Sports Team"),
        new(602,6,5,"Harrison Track Athlete Portraits","Ready for Delivery",-1,"National Stadium",48000,48000,"demo.sam","Sports Portrait"),
        new(603,6,9,"NCA Swim Team","Completed",-14,"National Aquatic Centre",110000,110000,"demo.michael","Sports Team"),
        new(107,1,6,"Peterson Maternity Portraits","Completed",-60,"Hope Gardens",42000,42000,"demo.anna","Maternity"),
        new(205,2,8,"Thompson Birthday Celebration","Needs Scheduling",5,"Client Home",58000,14500,"demo.jason","Birthday"),
        new(206,2,11,"Harbour Awards Ceremony","Cancelled",7,"Jamaica Pegasus",0,0,"demo.michael","Awards")
    ];

    internal static bool Seed(NpgsqlConnection connection,NpgsqlTransaction transaction,string login,IFileStorage storage,Action<DemoSeedProgress>? report=null)
    {
        using(var check=new NpgsqlCommand("select exists(select 1 from studio_state where key='demo-data-v6-full-studio')",connection,transaction))
            if((bool)check.ExecuteScalar()!)return false;
        report?.Invoke(new(31, "Studio setup", 0, 0, "Creating clients, photographers, availability and workspaces."));
        var versionStorage=new PrefixedFileStorage(storage,"VersionStore");
        foreach(var workspace in Workspaces)
        {
            Guid id=Id(1,workspace.Number);
            Execute(connection,transaction,
                "insert into studio_workspaces(id,name,email_address,route_key,is_enabled,sort_order,created_at_utc,updated_at_utc) values($1,$2,null,$3,true,$4,now(),now()) on conflict(id) do update set name=excluded.name,is_enabled=true,sort_order=excluded.sort_order,updated_at_utc=now()",
                id,workspace.Name,$"studio-demo-full-{workspace.Number}",100+workspace.Number);
            Execute(connection,transaction,
                "insert into studio_workspace_members(workspace_id,user_name,display_name,is_active,access_workspace,can_view,reply_requester,view_attachments,add_attachments,create_job,edit_job,manage_schedule,manage_production,notify_members,edit_workspace,manage_members,admin_workspace,send_as_self,created_at_utc,updated_at_utc) values($1,$2,$2,true,true,true,true,true,true,true,true,true,true,true,true,true,true,false,now(),now()) on conflict(workspace_id,user_name) do update set display_name=excluded.display_name,is_active=true,access_workspace=true,can_view=true,reply_requester=true,view_attachments=true,add_attachments=true,create_job=true,edit_job=true,manage_schedule=true,manage_production=true,notify_members=true,edit_workspace=true,manage_members=true,admin_workspace=true,updated_at_utc=now()",
                id,login);
        }

        foreach(var client in Clients)
            Execute(connection,transaction,
                "insert into studio_clients(id,display_name,email,phone,updated_at_utc) values($1,$2,$3,$4,now()) on conflict(id) do update set display_name=excluded.display_name,email=excluded.email,phone=excluded.phone,updated_at_utc=now()",
                Id(40,client.Number),client.Name,client.Email,client.Phone);

        SeedResourcesAndPhotographers(connection,transaction);

        int ordinal=0;
        foreach(var job in Jobs)
        {
            ordinal++;
            report?.Invoke(new(32 + 63 * (ordinal-1) / Jobs.Length, "Studio jobs", ordinal-1, Jobs.Length, $"Job {ordinal}/{Jobs.Length}: {job.Title}"));
            Guid jobId=Id(41,job.Number);
            Guid conversationId=Id(42,job.Number);
            Guid workspaceId=Id(1,job.Workspace);
            Guid clientId=Id(40,job.Client);
            var client=Clients.Single(x=>x.Number==job.Client);
            DateTimeOffset start=new DateTimeOffset(DateTime.UtcNow.Date,TimeSpan.Zero).AddDays(job.DayOffset).AddHours(14+(ordinal%5));
            DateTimeOffset end=start.AddHours(job.Type.Contains("Wedding",StringComparison.OrdinalIgnoreCase)?6:job.Type.Contains("Graduation",StringComparison.OrdinalIgnoreCase)?3:2);

            Execute(connection,transaction,
                "insert into studio_conversations(id,workspace_id,subject,contact_name,contact_address,channel,is_closed,created_at_utc) values($1,$2,$3,$4,$5,$6,false,now()-($7 || ' days')::interval) on conflict(id) do update set subject=excluded.subject,contact_name=excluded.contact_name,contact_address=excluded.contact_address,channel=excluded.channel",
                conversationId,workspaceId,job.Title,client.Name,client.Email,ordinal%4==0?"Phone":ordinal%3==0?"Portal":"Email",Math.Abs(job.DayOffset)+2);

            Execute(connection,transaction,
                "insert into studio_jobs(id,workspace_id,conversation_id,title,client_name,status,event_date,created_at_utc,assigned_to,client_id) values($1,$2,$3,$4,$5,$6,$7,now()-($8 || ' days')::interval,$9,$10) on conflict(id) do update set workspace_id=excluded.workspace_id,conversation_id=excluded.conversation_id,title=excluded.title,client_name=excluded.client_name,status=excluded.status,event_date=excluded.event_date,assigned_to=excluded.assigned_to,client_id=excluded.client_id",
                jobId,workspaceId,conversationId,job.Title,client.Name,NormalizeStatus(job.Status),DateOnly.FromDateTime(start.UtcDateTime),Math.Abs(job.DayOffset)+5,job.Photographer,clientId);

            Execute(connection,transaction,"update studio_conversations set job_id=$2 where id=$1",conversationId,jobId);

            SeedConversation(connection,transaction,job,client,conversationId,ordinal);
            SeedSubjectsAndCollections(connection,transaction,job,jobId,ordinal,start);
            SeedMedia(connection,transaction,versionStorage,job,jobId,login,ordinal,report);
            SeedSchedule(connection,transaction,job,jobId,start,end,ordinal);
            SeedFinance(connection,transaction,job,jobId,ordinal);
            SeedActivities(connection,transaction,job,jobId,ordinal,login);
            SeedNotifications(connection,transaction,job,login,ordinal);
            SeedAudit(connection,transaction,job,jobId,ordinal);
            SeedBooking(connection,transaction,job,jobId,start,end,ordinal);
            SeedDocument(connection,transaction,storage,job,conversationId,login);
        }

        report?.Invoke(new(96, "Front Desk and Finance", Jobs.Length, Jobs.Length, "Creating pending enquiries, bookings and invoices."));
        SeedFrontDeskRequests(connection,transaction);
        SeedInvoices(connection,transaction);

        Execute(connection,transaction,
            "insert into studio_state(key,value,updated_at_utc) values('demo-data-v6-full-studio',$1,now()) on conflict(key) do update set value=excluded.value,updated_at_utc=excluded.updated_at_utc",login);
        return true;
    }

    private static void SeedResourcesAndPhotographers(NpgsqlConnection c,NpgsqlTransaction tx)
    {
        var people=new[]
        {
            ("demo.sam","Samantha Lee","Employee",new[]{"Wedding","Portrait","Graduation"}),
            ("demo.jason","Jason Brown","Employee",new[]{"Corporate","ID","Sports"}),
            ("demo.anna","Anna Clarke","PartTime",new[]{"Newborn","School","Events"}),
            ("demo.michael","Michael Grant","Contractor",new[]{"Commercial","Corporate","Events"})
        };
        int i=0;
        foreach(var p in people)
        {
            i++;
            Guid resource=Id(43,i);
            Execute(c,tx,"insert into studio_schedule_resources(id,name,resource_type,is_active,notes,created_at_utc) values($1,$2,'Staff',true,'DEMO photographer',now()) on conflict(id) do update set name=excluded.name,is_active=true",resource,p.Item2);
            Execute(c,tx,"insert into studio_photographers(user_name,display_name,employment_type,is_active,skills,default_equipment_notes,schedule_resource_id,created_at_utc,updated_at_utc) values($1,$2,$3,true,$4,'DEMO kit', $5,now(),now()) on conflict(user_name) do update set display_name=excluded.display_name,employment_type=excluded.employment_type,is_active=true,skills=excluded.skills,schedule_resource_id=excluded.schedule_resource_id,updated_at_utc=now()",p.Item1,p.Item2,p.Item3,p.Item4,resource);
        }

        Execute(c,tx,"insert into studio_photographer_availability(id,user_name,starts_at_utc,ends_at_utc,availability_type,note,created_at_utc) values($1,'demo.anna',date_trunc('day',now())+interval '1 day',date_trunc('day',now())+interval '2 day','Unavailable','Day off',now()) on conflict(id) do nothing",Id(44,1));
        Execute(c,tx,"insert into studio_photographer_availability(id,user_name,starts_at_utc,ends_at_utc,availability_type,note,created_at_utc) values($1,'demo.jason',date_trunc('day',now())+interval '3 day 8 hours',date_trunc('day',now())+interval '3 day 13 hours','Unavailable','External assignment',now()) on conflict(id) do nothing",Id(44,2));
        Execute(c,tx,"insert into studio_photographer_availability(id,user_name,starts_at_utc,ends_at_utc,availability_type,note,created_at_utc) values($1,'demo.michael',date_trunc('day',now())+interval '2 day 12 hours',date_trunc('day',now())+interval '2 day 17 hours','Unavailable','Equipment service window',now()) on conflict(id) do nothing",Id(44,3));

        var resources=new[]
        {
            (10,"Studio A","Room","Primary portrait studio"),
            (11,"Studio B","Room","Headshots and ID studio"),
            (12,"Canon PRO-1000","Equipment","Large photo printer - Available"),
            (13,"ID Card Printer","Equipment","ID output device - In Use"),
            (14,"Retouch Workstation 1","Equipment","Production workstation - Available"),
            (15,"Photo Printer 2","Equipment","Maintenance / error demo state")
        };
        foreach(var r in resources)
            Execute(c,tx,"insert into studio_schedule_resources(id,name,resource_type,is_active,notes,created_at_utc) values($1,$2,$3,true,$4,now()) on conflict(id) do update set name=excluded.name,resource_type=excluded.resource_type,is_active=true,notes=excluded.notes",Id(43,r.Item1),r.Item2,r.Item3,r.Item4);
    }

    private static void SeedSubjectsAndCollections(NpgsqlConnection c,NpgsqlTransaction tx,DemoJob job,Guid jobId,int ordinal,DateTimeOffset scheduled)
    {
        Execute(c,tx,"insert into studio_media_galleries(id,name,default_lens,default_collection) values($1,'Media','Auto','Unclassified') on conflict(id) do nothing",jobId);
        string[] collections=job.Type.Contains("Wedding",StringComparison.OrdinalIgnoreCase)?["Getting Ready","Ceremony","Portraits","Reception"]:
            job.Type.Contains("Graduation",StringComparison.OrdinalIgnoreCase)||job.Type.Contains("School",StringComparison.OrdinalIgnoreCase)?["Roster","Stage","Groups","Unclassified"]:
            job.Type.Contains("Corporate",StringComparison.OrdinalIgnoreCase)||job.Type.Contains("Headshot",StringComparison.OrdinalIgnoreCase)?["Executives","Team","Selects","Unclassified"]:
            ["Selects","Proofs","Finals","Unclassified"];
        foreach(string collection in collections)
            Execute(c,tx,"insert into studio_media_collections(job_id,name) values($1,$2) on conflict do nothing",jobId,collection);

        int subjectCount=DemoSubjectCount(job);
        for(int i=0;i<subjectCount;i++)
        {
            Guid subject=MediaId(59,job.Number,i);
            string[] given=["Alicia","Jordan","Samantha","Jason","Amelia","Daniel","Emily","Michael","Anna","Ethan","Sarah","James","Chloe","Liam","Naomi","Noah","Grace","Joshua","Leah","Matthew"];
            string[] family=["Brown","Williams","Clarke","Campbell","Thompson","Henry","Grant","Wright","Johnson","Robinson","Miller","Wilson","Reid","Walker","Smith","Lewis","Morgan","Carter","Peterson","Harrison"];
            string name=$"{given[i%given.Length]} {family[(i/given.Length+job.Number)%family.Length]}";
            string reference=$"{job.Number}-{i+1:000}";
            string attributes=JsonSerializer.Serialize(new { rosterId=reference, category=job.Type, demo=true });
            string subjectStatus=job.Number==106
                ? i<4?"Not Photographed":i<7?"Needs Retake":i<11?"Needs Editing":i<15?"Ready for Production":"Ready"
                : job.DayOffset<0?"Photographed":"Not Photographed";
            Execute(c,tx,"insert into studio_job_subjects(id,job_id,gallery_id,name,reference,notes,updated_by,production_status,attributes,collection_name,scheduled_at_utc,schedule_location,contact_name,contact_email,contact_phone,photo_notes,is_system) values($1,$2,$2,$3,$4,$5,'demo-seed',$6,$7::jsonb,$8,$9,$10,'','','',$11,false) on conflict(id) do update set name=excluded.name,reference=excluded.reference,production_status=excluded.production_status,attributes=excluded.attributes,collection_name=excluded.collection_name,scheduled_at_utc=excluded.scheduled_at_utc,schedule_location=excluded.schedule_location,photo_notes=excluded.photo_notes,updated_at_utc=now()",
                subject,jobId,name,reference,$"Roster / subject for {job.Type}",subjectStatus,attributes,collections[Math.Min(i%collections.Length,collections.Length-1)],scheduled,job.Location,$"Capture requirements for {job.Type}");
        }
    }

    private static void SeedMedia(NpgsqlConnection c,NpgsqlTransaction tx,IFileStorage storage,DemoJob job,Guid jobId,string login,int ordinal,Action<DemoSeedProgress>? report)
    {
        int count=MediaCount(job);
        string[] statuses=["Needs Sorting","Needs Editing","Needs Review","Needs Approval","Ready","Ready for Production","Needs Print","Needs QC","Needs Delivery","Completed","Blocked","Needs Retake"];
        string[] collections=job.Type.Contains("Wedding",StringComparison.OrdinalIgnoreCase)?["Getting Ready","Ceremony","Portraits","Reception"]:
            job.Type.Contains("Graduation",StringComparison.OrdinalIgnoreCase)||job.Type.Contains("School",StringComparison.OrdinalIgnoreCase)?["Roster","Stage","Groups","Unclassified"]:
            job.Type.Contains("Corporate",StringComparison.OrdinalIgnoreCase)||job.Type.Contains("Headshot",StringComparison.OrdinalIgnoreCase)?["Executives","Team","Selects","Unclassified"]:
            ["Selects","Proofs","Finals","Unclassified"];

        int sourceCount=PhysicalSourceCount(job);
        for(int i=0;i<count;i++)
        {
            report?.Invoke(new(32 + (int)(63 * (ordinal-1 + (double)i / count) / Jobs.Length), "Studio photographs", i, count, $"Job {ordinal}/{Jobs.Length}: {job.Title} — downloading and storing photo {i+1}/{count}."));
            string key=$"demo-full/{job.Number}/asset-{i+1:000}";
            Guid versionId=MediaId(64,job.Number,i);
            // Large portrait jobs use different real source photos for every asset.
            // Revisions preserve the same photograph and stable asset identity.
            Guid original=DemoDataExpansion.AddImage(c,tx,storage,jobId,key,versionId,1,SelectPhotoVariant(job,i%sourceCount),null,login,sourceUrl:PortraitSource(job,i));
            if(i%50==0)DemoDataExpansion.AddImage(c,tx,storage,jobId,key,MediaId(66,job.Number,i),2,SelectPhotoVariant(job,i%sourceCount),original,login,sourceUrl:PortraitSource(job,i));
            Guid assetId=MediaId(65,job.Number,i);
            string status=statuses[(ordinal+i)%statuses.Length];
            assetId=ExecuteGuid(c,tx,"insert into studio_media_assets(id,job_id,gallery_id,asset_key,production_status,assigned_to,capture_at_utc) values($1,$2,$2,$3,$4,$5,now()-($6 || ' hours')::interval) on conflict(job_id,asset_key) do update set production_status=excluded.production_status,assigned_to=excluded.assigned_to,capture_at_utc=excluded.capture_at_utc returning id",
                assetId,jobId,key,status,job.Photographer,(i+1).ToString());
            Execute(c,tx,"insert into studio_job_asset_collections(job_id,asset_key,collection_name) values($1,$2,$3) on conflict(job_id,asset_key) do update set collection_name=excluded.collection_name",
                jobId,key,collections[i%collections.Length]);
            int subjectCount=DemoSubjectCount(job);
            int subjectIndex=job.Number==106 ? 4+(i%Math.Max(1,subjectCount-4)) : i%Math.Max(1,subjectCount);
            Guid subjectId=MediaId(59,job.Number,subjectIndex);
            Execute(c,tx,"insert into studio_media_asset_subjects(asset_id,subject_id) values($1,$2) on conflict do nothing",assetId,subjectId);
        }
    }

    private static int MediaCount(DemoJob job)
    {
        return job.Number switch
        {
            106 => 180, // employee ID job
            201 => 2, // two distinct licensed wedding photographs
            303 => 100, // school portraits
            301 => 120, // executive headshots
            401 => 241, // large roster: 241 distinct photographed subjects
            501 => 1,   // one genuine product photograph
            _ => IsPortraitCorpus(job)?8:PhysicalSourceCount(job)
        };
    }

    internal static int RepairMedia(NpgsqlConnection c,NpgsqlTransaction tx,string login,IFileStorage storage,Action<DemoSeedProgress>? report)
    {
        int restored=0,ordinal=0;var versions=new PrefixedFileStorage(storage,"VersionStore");
        foreach(var job in Jobs)
        {
            ordinal++;int count=MediaCount(job),sourceCount=PhysicalSourceCount(job);Guid jobId=Id(41,job.Number);
            using(var old=new NpgsqlCommand("select coalesce(max(substring(asset_key from 'asset-([0-9]+)$')::int),0) from studio_asset_versions where job_id=$1 and asset_key like $2",c,tx)){old.Parameters.AddWithValue(jobId);old.Parameters.AddWithValue($"demo-full/{job.Number}/asset-%");count=Math.Max(count,Convert.ToInt32(old.ExecuteScalar()));}
            for(int asset=0;asset<count;asset++)
            {
                report?.Invoke(new(32+(int)(63*(ordinal-1+(double)asset/count)/Jobs.Length),"Checking demo photographs",asset,count,$"{job.Title}: checking photo {asset+1}/{count}."));
                string key=$"demo-full/{job.Number}/asset-{asset+1:000}";
                Guid original=DemoDataExpansion.AddImage(c,tx,versions,jobId,key,MediaId(64,job.Number,asset),1,SelectPhotoVariant(job,asset%sourceCount),null,login,true,()=>restored++,PortraitSource(job,asset));
                if(asset%50==0 && original!=Guid.Empty)DemoDataExpansion.AddImage(c,tx,versions,jobId,key,MediaId(66,job.Number,asset),2,SelectPhotoVariant(job,asset%sourceCount),original,login,true,()=>restored++,PortraitSource(job,asset));
            }
        }
        return restored;
    }

    private static int DemoSubjectCount(DemoJob job)
    {
        return job.Number switch
        {
            106 => 50,
            401 => 327,
            303 => 60,
            301 => 24,
            201 => 8,
            _ => job.Type.Contains("Graduation",StringComparison.OrdinalIgnoreCase)||job.Type.Contains("School",StringComparison.OrdinalIgnoreCase)?24:
                 job.Type.Contains("Wedding",StringComparison.OrdinalIgnoreCase)?8:
                 job.Type.Contains("Team",StringComparison.OrdinalIgnoreCase)||job.Type.Contains("Group",StringComparison.OrdinalIgnoreCase)?12:
                 job.Type.Contains("Corporate",StringComparison.OrdinalIgnoreCase)||job.Type.Contains("Headshot",StringComparison.OrdinalIgnoreCase)||job.Type.Contains("ID",StringComparison.OrdinalIgnoreCase)?10:4
        };
    }

    private static string NormalizeStatus(string status)=>status switch
    {
        "Production" or "QC" or "Printing"=>"In Production",
        "Editing" or "Culling" or "Retouching"=>"Changes Requested",
        "Client Review"=>"Needs Review",
        "Ready for Delivery"=>"Ready for Client",
        "Booking"=>"Needs Scheduling",
        _=>status
    };
    private static bool IsPortraitCorpus(DemoJob job)=>job.Number is 106 or 301 or 303 or 401||
        job.Type.Contains("Headshot",StringComparison.OrdinalIgnoreCase)||job.Type.Contains("ID",StringComparison.OrdinalIgnoreCase)||
        job.Type.Contains("Passport",StringComparison.OrdinalIgnoreCase)||job.Type.Contains("Corporate Portrait",StringComparison.OrdinalIgnoreCase)||
        job.Type.Contains("Graduation",StringComparison.OrdinalIgnoreCase)||job.Type.Contains("School",StringComparison.OrdinalIgnoreCase);
    private static string? PortraitSource(DemoJob job,int asset)
    {
        if(!IsPortraitCorpus(job))return null;
        int offset=job.Number switch{106=>0,301=>180,303=>300,401=>400,_=>641+Array.FindIndex(Jobs.Where(j=>IsPortraitCorpus(j)&&j.Number is not(106 or 301 or 303 or 401)).ToArray(),j=>j.Number==job.Number)*8};
        return DemoPortraitCatalogue.Url(offset+asset);
    }

    private static int PhysicalSourceCount(DemoJob job)
    {
        if(job.Type.Contains("Wedding",StringComparison.OrdinalIgnoreCase)||job.Type.Contains("Engagement",StringComparison.OrdinalIgnoreCase)) return 2;
        if(job.Type.Contains("Graduation",StringComparison.OrdinalIgnoreCase)||job.Type.Contains("School",StringComparison.OrdinalIgnoreCase)) return 4;
        if(job.Type.Contains("Headshot",StringComparison.OrdinalIgnoreCase)||job.Type.Contains("ID",StringComparison.OrdinalIgnoreCase)||job.Type.Contains("Passport",StringComparison.OrdinalIgnoreCase)||job.Type.Contains("Corporate Portrait",StringComparison.OrdinalIgnoreCase)) return 2;
        if(job.Type.Contains("Family",StringComparison.OrdinalIgnoreCase)) return 1;
        return 1;
    }

    private static int SelectPhotoVariant(DemoJob job,int index)
    {
        // Indices map to the curated real-photo catalogue in DemoDataExpansion.
        if(job.Type.Contains("Wedding",StringComparison.OrdinalIgnoreCase)||job.Type.Contains("Engagement",StringComparison.OrdinalIgnoreCase)) return 10+(index%2);
        if(job.Type.Contains("Graduation",StringComparison.OrdinalIgnoreCase)||job.Type.Contains("School",StringComparison.OrdinalIgnoreCase)) return index%4;
        if(job.Type.Contains("Headshot",StringComparison.OrdinalIgnoreCase)||job.Type.Contains("ID",StringComparison.OrdinalIgnoreCase)||job.Type.Contains("Passport",StringComparison.OrdinalIgnoreCase)||job.Type.Contains("Corporate Portrait",StringComparison.OrdinalIgnoreCase)) return 4+(index%2);
        if(job.Type.Contains("Product",StringComparison.OrdinalIgnoreCase)||job.Type.Contains("Commercial",StringComparison.OrdinalIgnoreCase)||job.Type.Contains("Campaign",StringComparison.OrdinalIgnoreCase)) return 9;
        if(job.Type.Contains("Sports",StringComparison.OrdinalIgnoreCase)||job.Type.Contains("Team",StringComparison.OrdinalIgnoreCase)||job.Type.Contains("Athlete",StringComparison.OrdinalIgnoreCase)) return 8;
        if(job.Type.Contains("Newborn",StringComparison.OrdinalIgnoreCase)) return 7;
        if(job.Type.Contains("Family",StringComparison.OrdinalIgnoreCase)) return 12;
        if(job.Type.Contains("Portrait",StringComparison.OrdinalIgnoreCase)||job.Type.Contains("Senior",StringComparison.OrdinalIgnoreCase)) return 4+(index%2);
        return 4+(index%2);
    }

    private static void SeedConversation(NpgsqlConnection c,NpgsqlTransaction tx,DemoJob job,DemoClient client,Guid conversation,int ordinal)
    {
        Execute(c,tx,"insert into studio_messages(id,conversation_id,author_display_name,visibility,body,created_at_utc) values($1,$2,$3,'External',$4,now()-interval '2 days') on conflict(id) do update set body=excluded.body",
            Id(45,job.Number),conversation,client.Name,$"Request for {job.Type}. Please confirm schedule, package details and delivery timing.");
        Execute(c,tx,"insert into studio_messages(id,conversation_id,author_display_name,visibility,body,created_at_utc) values($1,$2,'Bryans Studio','Internal',$3,now()-interval '1 day') on conflict(id) do update set body=excluded.body",
            Id(46,job.Number),conversation,$"Team note: {job.Title} is currently {job.Status}. Confirm next action and customer communication.");
        if(ordinal%3==0)
            Execute(c,tx,"insert into studio_messages(id,conversation_id,author_display_name,visibility,body,created_at_utc) values($1,$2,$3,'External','Can you confirm the latest status and whether anything is needed from us?',now()-interval '4 hours') on conflict(id) do nothing",
                Id(47,job.Number),conversation,client.Name);
    }

    private static void SeedSchedule(NpgsqlConnection c,NpgsqlTransaction tx,DemoJob job,Guid jobId,DateTimeOffset start,DateTimeOffset end,int ordinal)
    {
        Guid schedule=Id(48,job.Number);
        bool shotComplete=job.DayOffset<0||NormalizeStatus(job.Status) is "In Production" or "Ready for Client" or "Completed";
        string status=job.Status=="Cancelled"?"Cancelled":shotComplete?"Completed":job.Status=="Photography"?"CheckedIn":job.Status=="Booking"?"Requested":"Scheduled";
        Execute(c,tx,"insert into studio_schedule_entries(id,job_id,title,starts_at_utc,ends_at_utc,location,status,notes,created_at_utc) values($1,$2,$3,$4,$5,$6,$7,$8,now()) on conflict(id) do update set title=excluded.title,starts_at_utc=excluded.starts_at_utc,ends_at_utc=excluded.ends_at_utc,location=excluded.location,status=excluded.status,notes=excluded.notes",
            schedule,jobId,job.Title,start,end,job.Location,status,$"DEMO {job.Type} shoot");
        Execute(c,tx,"insert into studio_shoot_sessions(id,job_id,schedule_entry_id,photographer_user_name,assistant_user_name,call_time_utc,planned_end_utc,location,status,image_count,backup_confirmed,reshoot_required,coverage_notes,equipment_notes,handoff_status,created_at_utc,updated_at_utc) values($1,$2,$3,$4,$5,$6,$7,$8,$9,$10,$11,$12,$13,'DEMO camera kit',$14,now(),now()) on conflict(id) do update set photographer_user_name=excluded.photographer_user_name,call_time_utc=excluded.call_time_utc,planned_end_utc=excluded.planned_end_utc,location=excluded.location,status=excluded.status,image_count=excluded.image_count,backup_confirmed=excluded.backup_confirmed,reshoot_required=excluded.reshoot_required,handoff_status=excluded.handoff_status,updated_at_utc=now()",
            Id(49,job.Number),jobId,schedule,job.Photographer,ordinal%4==0?"demo.anna":DBNull.Value,start,end,job.Location,job.Status=="Cancelled"?"Cancelled":shotComplete?"ShootComplete":job.Status=="Photography"?"InProgress":"Scheduled",
            shotComplete||job.Status=="Photography"?MediaCount(job):0,shotComplete,ordinal%11==0,$"Coverage plan for {job.Type}",shotComplete?"Ready":"NotReady");

        if(shotComplete)Execute(c,tx,"update studio_shoot_sessions set checked_in_at_utc=call_time_utc,checked_out_at_utc=planned_end_utc where id=$1",Id(49,job.Number));
        // Demonstrate a true multi-session job and schedule exception states.
        if(job.Number==201)
        {
            DateTimeOffset prepStart=start.AddDays(-1).AddHours(-3);
            DateTimeOffset prepEnd=prepStart.AddHours(2);
            Guid prepSchedule=Id(62,job.Number);
            Execute(c,tx,"insert into studio_schedule_entries(id,job_id,title,starts_at_utc,ends_at_utc,location,status,notes,created_at_utc) values($1,$2,'Carter Wedding - rehearsal / venue walk-through',$3,$4,$5,'Completed','DEMO multi-session wedding preparation',now()) on conflict(id) do update set starts_at_utc=excluded.starts_at_utc,ends_at_utc=excluded.ends_at_utc,location=excluded.location,status=excluded.status",
                prepSchedule,jobId,prepStart,prepEnd,job.Location);
            Execute(c,tx,"insert into studio_shoot_sessions(id,job_id,schedule_entry_id,photographer_user_name,assistant_user_name,call_time_utc,planned_end_utc,location,status,image_count,backup_confirmed,reshoot_required,coverage_notes,equipment_notes,handoff_status,created_at_utc,updated_at_utc) values($1,$2,$3,'demo.sam','demo.anna',$4,$5,$6,'Completed',42,true,false,'Venue and lighting walk-through','DEMO camera kit','Ready',now(),now()) on conflict(id) do update set call_time_utc=excluded.call_time_utc,planned_end_utc=excluded.planned_end_utc,status='Completed',updated_at_utc=now()",
                Id(63,job.Number),jobId,prepSchedule,prepStart,prepEnd,job.Location);
        }
        if(job.Number==403)
        {
            Guid cancelled=Id(62,job.Number);
            DateTimeOffset oldStart=start.AddDays(-2);
            Execute(c,tx,"insert into studio_schedule_entries(id,job_id,title,starts_at_utc,ends_at_utc,location,status,notes,created_at_utc) values($1,$2,'Campion Graduation Retakes - original slot',$3,$4,$5,'Cancelled','DEMO cancelled slot after reschedule',now()) on conflict(id) do update set status='Cancelled',notes=excluded.notes",
                cancelled,jobId,oldStart,oldStart.AddHours(2),job.Location);
        }
    }

    private static void SeedFinance(NpgsqlConnection c,NpgsqlTransaction tx,DemoJob job,Guid jobId,int ordinal)
    {
        Execute(c,tx,"insert into studio_financial_events(id,job_id,kind,amount,note,created_at_utc) values($1,$2,'Charge',$3,'DEMO package charge',now()-interval '6 days') on conflict(id) do update set amount=excluded.amount,note=excluded.note",
            Id(50,job.Number),jobId,job.Price);
        if(job.Paid>0)
            Execute(c,tx,"insert into studio_financial_events(id,job_id,kind,amount,note,created_at_utc) values($1,$2,$3,$4,$5,now()-interval '4 days') on conflict(id) do update set amount=excluded.amount,note=excluded.note",
                Id(51,job.Number),jobId,job.Paid==job.Price?"Payment":"Deposit",job.Number==201?job.Paid/2:job.Paid,ordinal%2==0?"DEMO card payment":"DEMO bank transfer");
        if(job.Number==201)
            Execute(c,tx,"insert into studio_financial_events(id,job_id,kind,amount,note,created_at_utc) values($1,$2,'Payment',$3,'Second wedding payment - bank transfer',now()-interval '2 days') on conflict(id) do nothing",Id(73,job.Number),jobId,job.Paid/2);
        if(ordinal%10==0)
            Execute(c,tx,"insert into studio_financial_events(id,job_id,kind,amount,note,created_at_utc) values($1,$2,'Refund',$3,'DEMO partial refund',now()-interval '1 day') on conflict(id) do update set amount=excluded.amount",
                Id(52,job.Number),jobId,-5000m);
    }

    private static void SeedActivities(NpgsqlConnection c,NpgsqlTransaction tx,DemoJob job,Guid jobId,int ordinal,string login)
    {
        string[] stages=["FileIngest","Cull","Edit","Retouch","Proof","QC","Print","Delivery"];
        for(int i=0;i<3;i++)
        {
            string stage=stages[(ordinal+i)%stages.Length];
            string collection=job.Type=="Wedding"?new[]{"Ceremony","Portraits","Reception"}[i]:job.Type.Contains("Graduation")||job.Type.Contains("School")?new[]{"Roster","Stage","Groups"}[i]:job.Type.Contains("Corporate")||job.Type.Contains("Headshot")?new[]{"Executives","Team","Selects"}[i]:new[]{"Selects","Proofs","Finals"}[i];
            string[] workStates=["NotStarted","InProgress","Blocked","WaitingOnCustomer","ReworkRequired","Partial","Failed","ReprintRequired","ReadyForDelivery","Completed","Archived"];
            string status=job.Status=="Completed"?"Completed":workStates[(ordinal+i)%workStates.Length];
            string? blocker=status is "Blocked" or "WaitingOnCustomer"?"Waiting on customer selection":status is "Failed" or "ReprintRequired"?"Output failed; check the printer and approve a reprint":status=="ReworkRequired"?"QC requested another retouch pass":null;
            Execute(c,tx,"insert into studio_job_activities(id,job_id,activity_type,title,description,status,priority,assigned_to,scheduled_at_utc,due_at_utc,started_at_utc,completed_at_utc,location,blocked_reason,handoff_to,created_by,created_at_utc,updated_at_utc) values($1,$2,$3,$4,$5,$6,$7,$8,now()+($9 || ' hours')::interval,now()+($10 || ' days')::interval,$11,$12,$13,$14,null,'demo-seed',now(),now()) on conflict(id) do update set activity_type=excluded.activity_type,title=excluded.title,status=excluded.status,priority=excluded.priority,assigned_to=excluded.assigned_to,due_at_utc=excluded.due_at_utc,blocked_reason=excluded.blocked_reason,updated_at_utc=now()",
                Id(53,job.Number*10+i),jobId,stage,$"{stage}: {job.Title}",$"Production work for {job.Type}",status,ordinal%7==0?"High":ordinal%4==0?"Low":"Normal",i==1?login:job.Photographer,(i*4).ToString(),(job.DayOffset+i+1).ToString(),
                status=="InProgress"?DateTimeOffset.UtcNow:DBNull.Value,status=="Completed"?DateTimeOffset.UtcNow.AddHours(-2):DBNull.Value,job.Location,(object?)blocker??DBNull.Value);
            Execute(c,tx,"update studio_job_activities set collection_name=$2 where id=$1",Id(53,job.Number*10+i),collection);
        }
    }

    private static void SeedAudit(NpgsqlConnection c,NpgsqlTransaction tx,DemoJob job,Guid jobId,int ordinal)
    {
        Execute(c,tx,"insert into studio_operational_audit(id,job_id,activity_id,action,user_name,detail,created_at_utc) values($1,$2,null,'BookingCreated','demo-seed',$3,now()-interval '8 days') on conflict(id) do nothing",Id(54,job.Number),jobId,$"Booking created for {job.Type}");
        Execute(c,tx,"insert into studio_operational_audit(id,job_id,activity_id,action,user_name,detail,created_at_utc) values($1,$2,null,'PhotographerAssigned','demo-seed',$3,now()-interval '5 days') on conflict(id) do nothing",Id(55,job.Number),jobId,$"Assigned to {job.Photographer}");
        if(job.DayOffset<=0)
            Execute(c,tx,"insert into studio_operational_audit(id,job_id,activity_id,action,user_name,detail,created_at_utc) values($1,$2,null,'ShootUpdated','demo-seed',$3,now()-interval '1 day') on conflict(id) do nothing",Id(56,job.Number),jobId,$"Shoot status: {job.Status}");
    }

    private static void SeedInvoices(NpgsqlConnection c,NpgsqlTransaction tx)
    {
        var invoiceJobs=new[] {101,201,301,304,401,501,601,603,503};

        for(int i=0;i<invoiceJobs.Length;i++)
        {
            var job=Jobs.Single(x=>x.Number==invoiceJobs[i]);
            var client=Clients.Single(x=>x.Number==job.Client);
            Guid invoice=Id(60,job.Number);
            decimal subtotal=job.Price;
            decimal tax=0m;
            decimal total=subtotal+tax;
            DateTimeOffset due=new DateTimeOffset(DateTime.UtcNow.Date,TimeSpan.Zero).AddDays(i%3==0?-3:7+i);
            string status=job.Status=="Booking"?"Draft":job.Paid>=job.Price?"Paid":due<DateTimeOffset.UtcNow?"Overdue":"Issued";
            Execute(c,tx,"insert into studio_account_invoices(id,invoice_number,job_id,client_name,client_email,currency,subtotal,tax_amount,total_amount,status,due_at_utc,issued_at_utc,paid_at_utc,created_by,created_at_utc,updated_at_utc) values($1,$2,$3,$4,$5,'JMD',$6,$7,$8,$9,$10,$11,$12,'demo-seed',now()-interval '10 days',now()) on conflict(id) do update set client_name=excluded.client_name,client_email=excluded.client_email,subtotal=excluded.subtotal,tax_amount=excluded.tax_amount,total_amount=excluded.total_amount,status=excluded.status,due_at_utc=excluded.due_at_utc,issued_at_utc=excluded.issued_at_utc,paid_at_utc=excluded.paid_at_utc,updated_at_utc=now()",
                invoice,$"DEMO-{DateTime.UtcNow:yyyy}-{job.Number}",Id(41,job.Number),client.Name,client.Email,subtotal,tax,total,status,due,
                status=="Draft"?DBNull.Value:DateTimeOffset.UtcNow.AddDays(-9),
                status=="Paid"?DateTimeOffset.UtcNow.AddDays(-2):DBNull.Value);
            Execute(c,tx,"insert into studio_account_invoice_jobs(invoice_id,job_id) values($1,$2) on conflict do nothing",invoice,Id(41,job.Number));
            Execute(c,tx,"insert into studio_account_invoice_lines(id,invoice_id,description,quantity,unit_price,line_total,sort_order) values($1,$2,$3,1,$4,$4,0) on conflict(id) do update set description=excluded.description,unit_price=excluded.unit_price,line_total=excluded.line_total",
                Id(61,job.Number),invoice,$"{job.Type} package",subtotal);
        }
    }

    private static void SeedFrontDeskRequests(NpgsqlConnection c,NpgsqlTransaction tx)
    {
        var samples=new[]
        {
            (701,1,1,"Family Portrait Enquiry","Pending",25000m,0m,2),
            (702,3,10,"KMG Executive Portrait Follow-up","AwaitingDeposit",50000m,25000m,3),
            (703,4,2,"Campion Graduation Retake Booking","AwaitingDeposit",15000m,5000m,8),
            (704,2,11,"Harbour Awards Night","Pending",40000m,0m,5)
        };
        foreach(var item in samples)
        {
            var client=Clients.Single(x=>x.Number==item.Item3);
            Guid id=Id(57,item.Item1);
            var payload=new
            {
                workspaceId=Id(1,item.Item2),
                clientName=client.Name,
                clientEmail=client.Email,
                title=item.Item4,
                eventDate=DateOnly.FromDateTime(DateTime.UtcNow.Date.AddDays(item.Item8)),
                requiredDeposit=item.Item6,
                source=item.Item1%2==0?"Website":"FrontDesk",
                currency="JMD",
                notes="DEMO front desk booking request",
                clientId=Id(40,item.Item3),
                externalBookingId=$"DEMO-{item.Item1}",
                startsAtUtc=new DateTimeOffset(DateTime.UtcNow.Date,TimeSpan.Zero).AddDays(item.Item8).AddHours(15),
                endsAtUtc=new DateTimeOffset(DateTime.UtcNow.Date,TimeSpan.Zero).AddDays(item.Item8).AddHours(17),
                location="Demo location",
                clientPhone=client.Phone
            };
            Execute(c,tx,"insert into studio_frontdesk_requests(id,workspace_id,payload,status,job_id,created_by,created_at_utc) values($1,$2,$3::jsonb,$4,null,'demo-seed',now()-interval '1 day') on conflict(id) do update set payload=excluded.payload,status=excluded.status",
                id,Id(1,item.Item2),JsonSerializer.Serialize(payload),item.Item5);
            if(item.Item7>0)
                Execute(c,tx,"insert into studio_frontdesk_deposits(id,request_id,amount,currency,provider,reference,recorded_by,created_at_utc) values($1,$2,$3,'JMD','demo','DEMO-RECEIPT-'||$4,'demo-seed',now()) on conflict(id) do update set amount=excluded.amount",
                    Id(58,item.Item1),id,item.Item7,item.Item1.ToString());
        }
    }

    private static void SeedBooking(NpgsqlConnection c,NpgsqlTransaction tx,DemoJob job,Guid jobId,DateTimeOffset start,DateTimeOffset end,int ordinal)
    {
        var client=Clients.Single(x=>x.Number==job.Client);
        Execute(c,tx,"insert into studio_bookings(id,job_id,workspace_id,client_name,client_email,client_phone,session_type,starts_at_utc,ends_at_utc,location,notes,source,status,external_booking_id,created_at_utc) values($1,$2,$3,$4,$5,$6,$7,$8,$9,$10,'Manual booking linked to Studio job',$11,$12,$13,now()-interval '10 days') on conflict(id) do nothing",
            Id(71,job.Number),jobId,Id(1,job.Workspace),client.Name,client.Email,client.Phone,job.Type,start,end,job.Location,ordinal%5,job.Status=="Cancelled"?4:job.DayOffset<0?3:2,$"DEMO-FULL-{job.Number}");
        Guid resource=Id(43,job.Photographer=="demo.sam"?1:job.Photographer=="demo.jason"?2:job.Photographer=="demo.anna"?3:4);
        Execute(c,tx,"insert into studio_schedule_entry_resources(entry_id,resource_id) values($1,$2) on conflict do nothing",Id(48,job.Number),resource);
        Execute(c,tx,"update studio_schedule_entries set booking_id=$2 where id=$1",Id(48,job.Number),Id(71,job.Number));
    }

    private static void SeedDocument(NpgsqlConnection c,NpgsqlTransaction tx,IFileStorage storage,DemoJob job,Guid conversation,string login)
    {
        Guid id=Id(72,job.Number);
        string key=$"demo/{id:N}.txt";
        byte[] bytes=System.Text.Encoding.UTF8.GetBytes($"{job.Title}\nCoverage: {job.Type}\nLocation: {job.Location}\nPackage charge: JMD {job.Price:N2}\nContact and delivery arrangements confirmed in the job conversation.\n");
        using var stream=new MemoryStream(bytes,false);
        var stored=new PrefixedFileStorage(storage,"Storage/attachments").StoreAsync(new StorageWriteRequest(key,stream,"text/plain","Session instructions.txt",login)).GetAwaiter().GetResult();
        Execute(c,tx,"insert into studio_attachments(id,conversation_id,file_name,stored_name,content_type,size_bytes,visibility,created_at_utc,sha256,uploaded_by,storage_status,source_key) values($1,$2,'Session instructions.txt',$3,'text/plain',$4,'Internal',now()-interval '2 days',$5,$6,'Stored',$7) on conflict(id) do nothing",id,conversation,key,stored.Length,stored.Sha256,login,$"demo-full-{job.Number}");
    }

    private static void SeedNotifications(NpgsqlConnection c,NpgsqlTransaction tx,DemoJob job,string login,int ordinal)
    {
        Execute(c,tx,"insert into studio_notifications(id,user_name,channel,event_type,subject,body,reference_type,reference_id,status,is_read,created_at_utc) values($1,$2,'InApp','WorkspaceMessage',$3,$4,'Job',$5,'Delivered',$6,now()-($7||' hours')::interval) on conflict(id) do nothing",
            Id(70,job.Number),login,job.Title,$"{job.Type}: please confirm the session, balance and delivery date.",Id(41,job.Number).ToString(),ordinal%3!=0,ordinal.ToString());
    }

    private static Guid ExecuteGuid(NpgsqlConnection c,NpgsqlTransaction tx,string sql,params object[] values)
    {
        using var command=new NpgsqlCommand(sql,c,tx);
        foreach(object value in values)command.Parameters.AddWithValue(value is DateTimeOffset timestamp?timestamp.ToUniversalTime():value);
        return (Guid)(command.ExecuteScalar()??throw new InvalidOperationException("Demo insert did not return an ID."));
    }

    private static Guid MediaId(int group,int jobNumber,int index)=>Id(group,checked(jobNumber*10000+index));

    private static Guid Id(int group,int number)=>Guid.Parse($"de000000-0000-4000-{group:0000}-{number:000000000000}");

    private static void Execute(NpgsqlConnection c,NpgsqlTransaction tx,string sql,params object[] values)
    {
        using var command=new NpgsqlCommand(sql,c,tx);
        foreach(object value in values) command.Parameters.AddWithValue(value is DateTimeOffset timestamp?timestamp.ToUniversalTime():value);
        command.ExecuteNonQuery();
    }
}


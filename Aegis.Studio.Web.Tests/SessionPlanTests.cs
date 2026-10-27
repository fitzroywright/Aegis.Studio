using System.Text.Json;
using Aegis.Studio.Web.Intake;
using Npgsql;
using Xunit;

namespace Aegis.Studio.Web.Tests;

public sealed class SessionPlanTests
{
    // Uses the same isolated PostgreSQL fixture as the existing WorkDay journeys.
    private static void Initialize(IntegratedWorkflowTests.WorkflowContext context)
    {
        using var input=typeof(IntakeSchema).Assembly.GetManifestResourceStream("Aegis.Studio.Web.Intake.IntakeSchema.sql")!;
        using var reader=new StreamReader(input);
        // Only the roster migration is needed for this subject-store journey.
        string schema=reader.ReadToEnd();
        string functions=schema[schema.IndexOf("create or replace function studio_apply_job_roster",StringComparison.Ordinal)..schema.IndexOf("create or replace function studio_promote_intake_roster",StringComparison.Ordinal)];
        Execute(context,"""
          alter table studio_job_subjects add column if not exists pickup_notification text not null default 'None';
          alter table studio_job_subjects add column if not exists preference_source text;
          alter table studio_job_subjects add column if not exists preference_at_utc timestamptz;
          create table if not exists studio_intake_rosters(conversation_id uuid primary key references studio_conversations(id),revision bigint not null default 0,rows jsonb not null default '[]',updated_by text not null,updated_at_utc timestamptz not null default now());
          create table if not exists studio_intake_roster_conflicts(conversation_id uuid primary key references studio_conversations(id),rows jsonb not null,revision bigint not null,created_at_utc timestamptz not null default now());
          """+functions);
    }
    [Fact]
    public void WorkbookWithDifferentDateEpochRequiresAnExplicitCsvConversion()
    {
        using var bytes=new MemoryStream();
        using(var zip=new System.IO.Compression.ZipArchive(bytes,System.IO.Compression.ZipArchiveMode.Create,true))
        using(var writer=new StreamWriter(zip.CreateEntry("xl/workbook.xml").Open()))
            writer.Write("<workbook xmlns='http://schemas.openxmlformats.org/spreadsheetml/2006/main'><workbookPr date1904='1'/></workbook>");
        bytes.Position=0;
        var error=Assert.Throws<ArgumentException>(()=>Aegis.Studio.Web.Media.RosterFileReader.Read(bytes,"subjects.xlsx"));
        Assert.Contains("1904",error.Message);
    }
    [StudioPostgresFact]
    public void JobPlanPreservesIdentityLinksAndRejectsStaleEditsFromOtherScreens()
    {
        using var context=new IntegratedWorkflowTests.WorkflowContext();Initialize(context);
        var store=new IntakeRosterStore(context.Database);var initial=store.ReadJob(context.Job);
        var saved=store.CommitJob(context.Job,new(initial.Revision,Rows(new{name="Alicia",reference="A1",collection="Class 1"})),"Manager",true);
        Guid subject=saved.Rows[0].GetProperty("subjectId").GetGuid();
        Execute(context,"insert into studio_media_galleries(id) values($1)",context.Job);
        Guid asset=Guid.NewGuid();Execute(context,"insert into studio_media_assets(id,job_id,gallery_id,asset_key) values($1,$2,$2,'session-plan-test')",asset,context.Job);
        Execute(context,"insert into studio_media_asset_subjects(asset_id,subject_id) values($1,$2)",asset,subject);
        var edited=store.CommitJob(context.Job,new(saved.Revision,Rows(new{subjectId=subject,name="Alicia Corrected",reference="A1",collection="Class 2",contactEmail="parent@example.com"})),"Manager",false);
        Assert.Equal(subject,edited.Rows[0].GetProperty("subjectId").GetGuid());
        using(var query=new NpgsqlCommand("select count(*) from studio_media_asset_subjects where subject_id=$1",context.Connection)){query.Parameters.AddWithValue(subject);Assert.Equal(1L,query.ExecuteScalar());}
        Assert.Throws<InvalidOperationException>(()=>store.CommitJob(context.Job,new(saved.Revision,edited.Rows),"Manager",false));
        Execute(context,"update studio_job_subjects set contact_phone='+18765550000',updated_at_utc=clock_timestamp() where id=$1",subject);
        Assert.Throws<InvalidOperationException>(()=>store.CommitJob(context.Job,new(edited.Revision,edited.Rows),"Manager",false));
        var current=store.ReadJob(context.Job);
        Assert.Throws<ArgumentException>(()=>store.CommitJob(context.Job,new(current.Revision,Rows()),"Manager",true));
        Assert.Throws<UnauthorizedAccessException>(()=>store.CommitJob(context.Job,new(current.Revision,Rows(new{subjectId=subject,name="Alicia",reference="A1",scheduledAtUtc="2026-11-03T14:00:00Z"})),"Manager",false));
    }
    [StudioPostgresFact]
    public void PlanDoesNotDependOnTheIntakeReverseLinkAndUpdatesLinkedIntakeRevision()
    {
        using var context=new IntegratedWorkflowTests.WorkflowContext();Initialize(context);
        // Legacy jobs can have the job-side conversation without the reverse link.
        Execute(context,"update studio_conversations set job_id=null where id=(select conversation_id from studio_jobs where id=$1)",context.Job);
        var store=new IntakeRosterStore(context.Database);var before=store.ReadJob(context.Job);
        var saved=store.CommitJob(context.Job,new(before.Revision,Rows(new{name="New Student",reference="N1"})),"Manager",false);
        Assert.Single(saved.Rows.EnumerateArray());
        using var query=new NpgsqlCommand("select r.revision,r.updated_by from studio_intake_rosters r join studio_jobs j on j.conversation_id=r.conversation_id where j.id=$1",context.Connection);
        query.Parameters.AddWithValue(context.Job);using var reader=query.ExecuteReader();Assert.True(reader.Read());Assert.Equal(1,reader.GetInt64(0));Assert.Equal("Manager",reader.GetString(1));
    }
    [StudioPostgresFact]
    public void ReviewingOneWebsiteChangeLeavesOtherConflictsVisible()
    {
        using var context=new IntegratedWorkflowTests.WorkflowContext();Initialize(context);
        Guid first=Guid.NewGuid(),second=Guid.NewGuid();
        foreach(var cv in new[]{first,second})
        {
            Execute(context,"insert into studio_conversations(id,workspace_id,job_id,subject,contact_name,contact_address,channel,created_at_utc) values($1,$2,$3,'Subject update','Client','client@example.test','Website',now())",cv,context.Workspace,context.Job);
            Execute(context,"insert into studio_intake_roster_conflicts(conversation_id,rows,revision) values($1,'[]'::jsonb,1)",cv);
        }
        var store=new IntakeRosterStore(context.Database);var view=store.ReadJob(context.Job);
        Assert.Equal(2,view.IncomingRows!.Value.GetArrayLength());
        var saved=store.CommitJob(context.Job,new(view.Revision,Rows(new{name="Student",reference="S1"}),true,first),"Manager",true);
        Assert.Single(saved.IncomingRows!.Value.EnumerateArray());
        Assert.Equal(second,saved.IncomingRows.Value[0].GetProperty("conversationId").GetGuid());
    }
    private static JsonElement Rows(params object[] rows)=>JsonSerializer.SerializeToElement(rows);
    private static void Execute(IntegratedWorkflowTests.WorkflowContext context,string sql,params object[] values)
    {
        using var command=new NpgsqlCommand(sql,context.Connection);foreach(var value in values)command.Parameters.AddWithValue(value);command.ExecuteNonQuery();
    }
}

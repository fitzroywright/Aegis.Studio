using Aegis.Studio.Web.SessionPlanning;
using Npgsql;
using Xunit;
namespace Aegis.Studio.Web.Tests;

public sealed class SubjectPlanStoreTests
{
    [StudioPostgresFact]
    public void PlanningRetainsIdentityRejectsStaleEditsAndChecksPhotoOwnership()
    {
        using var context = new IntegratedWorkflowTests.WorkflowContext();
        Guid subject = Guid.NewGuid();
        Run("insert into studio_job_subjects(id,job_id,name,reference,notes,updated_by) values($1,$2,'Same name','STABLE-001','','Test')", subject, context.Job);
        var store = new SubjectPlanStore(context.Database);
        var initial = new SubjectPlan(0, "Performers", [new("Production", "Autumn"), new("Cast", "A")], "", false, [new("Programme portrait", "Pending", [])]);
        var saved = store.Save(context.Job, subject, initial, "Planner", false);
        Assert.Equal(1L, saved.Revision);
        Assert.Throws<InvalidOperationException>(() => store.Save(context.Job, subject, initial, "Planner", false));
        Assert.Throws<KeyNotFoundException>(() => store.Save(Guid.NewGuid(), subject, saved, "Planner", false));
        var approved = saved with { IdentityConfirmed = true, Shots = [new("Programme portrait", "Approved", [Guid.NewGuid()], Guid.NewGuid())] };
        Assert.Throws<UnauthorizedAccessException>(() => store.Save(context.Job, subject, approved, "Planner", false));
        Assert.Throws<ArgumentException>(() => store.Save(context.Job, subject, approved, "Reviewer", true));
        Guid asset = Guid.NewGuid(), version = Guid.NewGuid();
        Run("insert into studio_media_galleries(id) values($1)", context.Job);
        Run("insert into studio_asset_versions(id,job_id,asset_key,version_number,file_name,content_type,size_bytes,sha256,stored_path,created_by,created_at_utc) values($1,$2,'plan-test',1,'Portrait.jpg','image/jpeg',1024,'test','plan-test','Test',now())", version, context.Job);
        Run("insert into studio_media_assets(id,job_id,gallery_id,asset_key) values($1,$2,$2,'plan-test')", asset, context.Job);
        Run("insert into studio_media_asset_subjects(asset_id,subject_id) values($1,$2)", asset, subject);
        var validApproval = saved with { IdentityConfirmed = true, Shots = [new("Programme portrait", "Approved", [asset], version)] };
        var approvedPlan = store.Save(context.Job, subject, validApproval, "Reviewer", true);
        Assert.Equal(version, approvedPlan.Shots[0].ApprovedVersionId);
        Assert.Equal(2L, approvedPlan.Revision);
        Assert.Throws<UnauthorizedAccessException>(() => store.Save(context.Job, subject, approvedPlan with { Shots = [new("Programme portrait", "Needs retake", [asset])] }, "Planner", false));
        using var read = new NpgsqlCommand("select reference from studio_job_subjects where id=$1", context.Connection);
        read.Parameters.AddWithValue(subject); Assert.Equal("STABLE-001", read.ExecuteScalar());
        void Run(string sql, params object[] values)
        { using var command = new NpgsqlCommand(sql, context.Connection); foreach (var value in values) command.Parameters.AddWithValue(value); command.ExecuteNonQuery(); }
    }
}

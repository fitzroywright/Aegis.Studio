using Aegis.Studio.Web.Persistence;
using Npgsql;
namespace Aegis.Studio.Web.Workflow;
public static class WorkflowSchema
{
    public static void Ensure(StudioDatabase database)
    {
        using var c=database.OpenConnection();using var tx=c.BeginTransaction();
        using var stream=typeof(WorkflowSchema).Assembly.GetManifestResourceStream("Aegis.Studio.Web.Workflow.WorkflowSchema.sql")??throw new InvalidOperationException("Workflow migration resource missing.");
        using var reader=new StreamReader(stream);using var cmd=new NpgsqlCommand(reader.ReadToEnd(),c,tx);cmd.ExecuteNonQuery();tx.Commit();
    }
}

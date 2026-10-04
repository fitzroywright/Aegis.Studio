using Npgsql;

namespace Aegis.Studio.Web.Production;

public static class JobShootLifecycle
{
    // Both Studio and WorkDay update the same shoot records and the existing Job.
    public static void Advance(NpgsqlConnection connection,NpgsqlTransaction transaction,Guid jobId,string actor)
    {
        using(var jobLock=new NpgsqlCommand("select id from studio_jobs where id=$1 for update",connection,transaction))
        {jobLock.Parameters.AddWithValue(jobId);jobLock.ExecuteScalar();}
        using var update=new NpgsqlCommand("""
            update studio_jobs set status=case
                when not exists(select 1 from studio_shoot_sessions where job_id=$1 and status<>'Cancelled' and checked_out_at_utc is null)
                     and exists(select 1 from studio_shoot_sessions where job_id=$1 and checked_out_at_utc is not null) then 'Awaiting Upload'
                when exists(select 1 from studio_shoot_sessions where job_id=$1 and status='InProgress') then 'InProgress'
                else status end
            where id=$1 and status in ('New','Needs Scheduling','Scheduled','Accepted','Arrived','InProgress')
            returning status,conversation_id
            """,connection,transaction);
        update.Parameters.AddWithValue(jobId);string? status=null;Guid conversation=Guid.Empty;
        using(var reader=update.ExecuteReader()){if(reader.Read()){status=reader.GetString(0);conversation=reader.GetGuid(1);}}
        if(status!="Awaiting Upload")return;
        using var audit=new NpgsqlCommand("""
            insert into studio_operational_audit(id,job_id,activity_id,action,user_name,detail,created_at_utc)
            values($1,$2,null,'AwaitingUpload',$3,'All scheduled shoots finished. Upload media for review.',now())
            """,connection,transaction);
        audit.Parameters.AddWithValue(Guid.NewGuid());audit.Parameters.AddWithValue(jobId);audit.Parameters.AddWithValue(actor);audit.ExecuteNonQuery();
        using var message=new NpgsqlCommand("insert into studio_messages(id,conversation_id,author_display_name,visibility,body,created_at_utc) values($1,$2,$3,'Internal','Shoot completed. Job is awaiting media upload.',now())",connection,transaction);
        message.Parameters.AddWithValue(Guid.NewGuid());message.Parameters.AddWithValue(conversation);message.Parameters.AddWithValue(actor);message.ExecuteNonQuery();
    }
}

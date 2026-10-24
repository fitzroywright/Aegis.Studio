using Aegis.Studio.Web.FrontDesk;
using Aegis.Studio.Web.Persistence;
using Npgsql;
using Xunit;

namespace Aegis.Studio.Web.Tests;

public sealed class FrontDeskBookingRequestTests
{
    [Theory]
    [InlineData(0,100,false)]
    [InlineData(99.99,100,false)]
    [InlineData(100,100,true)]
    [InlineData(125,100,true)]
    [InlineData(0,0,true)]
    [InlineData(-1,0,false)]
    public void ConfirmationRequiresTheConfiguredDeposit(decimal paid,decimal required,bool expected)
        => Assert.Equal(expected,FrontDeskRequestRules.DepositSatisfied(paid,required));

    [Theory]
    [InlineData(0,"JMD")]
    [InlineData(-1,"JMD")]
    [InlineData(1.001,"JMD")]
    [InlineData(1,"XX")]
    public void InvalidDepositMoneyIsRejected(decimal amount,string currency)
        => Assert.Throws<ArgumentException>(()=>FrontDeskRequestRules.ValidateMoney(amount,currency));

    [Fact]
    public void PublishedPriceSnapshotMustMatchThePendingDeposit()
    {
        var snapshot=new BookingPriceSnapshot("portrait","Portrait",3,12500,5000,"JMD",new Dictionary<string,int>{{"Photographer",1}});
        var request=new PendingBookingRequest(Guid.NewGuid(),"Client","client@example.invalid","Portrait",DateOnly.FromDateTime(DateTime.Today.AddDays(2)),50,"JMD",null,PriceSnapshot:snapshot);
        FrontDeskRequestRules.Validate(request);
        Assert.Throws<ArgumentException>(()=>FrontDeskRequestRules.Validate(request with{RequiredDeposit=49}));
    }

    [StudioPostgresFact]
    public async Task PendingJobExistsBeforeDepositAndConcurrentReceiptReplayDoesNotConfirm()
    {
        await using var fixture=await DatabaseFixture.Create();
        var store=new FrontDeskBookingRequestStore(fixture.Database);
        var request=store.Create(new(fixture.Workspace,"Morgan Campbell","morgan@example.invalid","Campbell Wedding",DateOnly.FromDateTime(DateTime.UtcNow.AddDays(30)),100,"JMD",null),"frontdesk");
        Assert.NotNull(request.JobId);Assert.Equal(1,fixture.Count("studio_jobs"));
        var partial=store.RecordDeposit(request.Id,new(40,"JMD","Bank","first-receipt"),"frontdesk");
        Assert.Equal(request.JobId,partial.JobId);Assert.Equal(40,partial.RecordedDeposit);
        var payment=new VerifiedDepositRequest(60,"JMD","Bank","second-receipt");
        var results=await Task.WhenAll(Task.Run(()=>store.RecordDeposit(request.Id,payment,"frontdesk")),Task.Run(()=>store.RecordDeposit(request.Id,payment,"frontdesk")));
        Assert.NotNull(results[0].JobId);Assert.Equal(results[0].JobId,results[1].JobId);
        Assert.Equal(1,fixture.Count("studio_jobs"));Assert.Equal(2,fixture.Count("studio_frontdesk_deposits"));Assert.Equal(2,fixture.Count("studio_financial_events"));
        using var connection=fixture.Database.OpenConnection();using var command=new NpgsqlCommand("select status from studio_jobs",connection);Assert.Equal("Pending",command.ExecuteScalar());
        Assert.All(results,x=>Assert.Equal("AwaitingConfirmation",x.Status));
        var other=store.Create(new(fixture.Workspace,"Alex","alex@example.invalid","Second wedding",DateOnly.FromDateTime(DateTime.UtcNow.AddDays(35)),100,"JMD",null),"frontdesk");
        Assert.Throws<ArgumentException>(()=>store.RecordDeposit(other.Id,payment,"frontdesk"));
        Assert.Equal(2,fixture.Count("studio_frontdesk_deposits"));
        await Task.WhenAll(
            Task.Run(()=>store.RecordDeposit(other.Id,new(40,"JMD","Bank","third-receipt"),"frontdesk")),
            Task.Run(()=>store.RecordDeposit(other.Id,new(60,"JMD","Bank","fourth-receipt"),"frontdesk")));
        var confirmed=store.GetAll().Single(x=>x.Id==other.Id);
        Assert.NotNull(confirmed.JobId);Assert.Equal(100,confirmed.RecordedDeposit);
        Assert.Equal(2,fixture.Count("studio_jobs"));Assert.Equal(4,fixture.Count("studio_financial_events"));
    }

    [StudioPostgresFact]
    public async Task ConcurrentWebsiteRetryCreatesOnePendingRequestAndOnePendingJob()
    {
        await using var fixture=await DatabaseFixture.Create();
        var store=new FrontDeskBookingRequestStore(fixture.Database);
        var request=new PendingBookingRequest(fixture.Workspace,"Morgan","morgan@example.invalid","Portrait",DateOnly.FromDateTime(DateTime.UtcNow.AddDays(2)),100,"JMD",null,ExternalBookingId:"website-001");
        var results=await Task.WhenAll(Task.Run(()=>store.Create(request,"website")),Task.Run(()=>store.Create(request,"website")));
        Assert.Equal(results[0].Id,results[1].Id);Assert.Equal(1,fixture.Count("studio_frontdesk_requests"));Assert.Equal(1,fixture.Count("studio_jobs"));
        Assert.NotNull(results[0].JobId);Assert.Equal(results[0].JobId,results[1].JobId);
        Assert.Throws<ArgumentException>(()=>store.Create(request with{ClientName="Someone else"},"website"));
    }

    private sealed class DatabaseFixture : IAsyncDisposable
    {
        private readonly string connectionString;
        private readonly string schema;
        private readonly NpgsqlDataSource source;
        public StudioDatabase Database {get;}
        public Guid Workspace {get;}=Guid.NewGuid();
        private DatabaseFixture(string connectionString,string schema,NpgsqlDataSource source)
        {this.connectionString=connectionString;this.schema=schema;this.source=source;Database=new(source);}
        public static async Task<DatabaseFixture> Create()
        {
            string connectionString=Environment.GetEnvironmentVariable("STUDIO_TEST_POSTGRES")!;
            string schema="studio_frontdesk_test_"+Guid.NewGuid().ToString("N");
            await using(var connection=new NpgsqlConnection(connectionString)){await connection.OpenAsync();await using var command=new NpgsqlCommand($"create schema {schema}",connection);await command.ExecuteNonQueryAsync();}
            var settings=new NpgsqlConnectionStringBuilder(connectionString){SearchPath=schema};
            var fixture=new DatabaseFixture(connectionString,schema,NpgsqlDataSource.Create(settings.ConnectionString));
            using var c=fixture.Database.OpenConnection();
            string[] sql=[
                "create table studio_workspaces(id uuid primary key,is_enabled boolean not null)",
                "create table studio_conversations(id uuid primary key,workspace_id uuid not null,subject text,contact_name text,contact_address text,channel text,is_closed boolean,created_at_utc timestamptz,job_id uuid)",
                "create table studio_jobs(id uuid primary key,workspace_id uuid,conversation_id uuid references studio_conversations(id),title text,client_name text,status text,event_date date,created_at_utc timestamptz,client_id uuid)",
                "create table studio_clients(id uuid primary key,display_name text,email text,phone text,updated_at_utc timestamptz default now())",
                "create table studio_messages(id uuid primary key,conversation_id uuid,author_display_name text,visibility text,body text,created_at_utc timestamptz)",
                "create table studio_financial_events(id uuid primary key,job_id uuid,kind text,amount numeric(18,2),note text,recorded_by text,created_at_utc timestamptz)",
                "create table studio_operational_audit(id uuid primary key,job_id uuid,activity_id uuid,action text,user_name text,detail text,created_at_utc timestamptz)"
            ];
            foreach(string statement in sql){using var command=new NpgsqlCommand(statement,c);command.ExecuteNonQuery();}
            using(var command=new NpgsqlCommand("insert into studio_workspaces values($1,true)",c)){command.Parameters.AddWithValue(fixture.Workspace);command.ExecuteNonQuery();}
            new FrontDeskBookingRequestStore(fixture.Database).EnsureSchema();return fixture;
        }
        public int Count(string table){using var c=Database.OpenConnection();using var command=new NpgsqlCommand($"select count(*) from {table}",c);return Convert.ToInt32(command.ExecuteScalar());}
        public async ValueTask DisposeAsync(){await source.DisposeAsync();await using var c=new NpgsqlConnection(connectionString);await c.OpenAsync();await using var command=new NpgsqlCommand($"drop schema {schema} cascade",c);await command.ExecuteNonQueryAsync();}
    }
}

public sealed class StudioPostgresFactAttribute : FactAttribute
{
    public StudioPostgresFactAttribute(){if(string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("STUDIO_TEST_POSTGRES")))Skip="Set STUDIO_TEST_POSTGRES to a disposable PostgreSQL test database to run transactional booking tests.";}
}


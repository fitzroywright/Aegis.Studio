using System.Security.Claims;
using Aegis.Studio.Application.EmailRouting;
using Aegis.Studio.Web.Email;
using Aegis.Studio.Web.Messaging;
using Aegis.Studio.Web.Operations;
using Aegis.Studio.Web.Persistence;
using Aegis.Studio.Web.Profiles;
using Microsoft.Extensions.Configuration;
using Npgsql;
using Xunit;

namespace Aegis.Studio.Web.Tests;

public sealed class MessagingRoutingTests
{
    [StudioPostgresFact]
    public async Task RouterSupportsPersonWorkspaceJobAndExternalReplyIdentity()
    {
        string connectionString = Environment.GetEnvironmentVariable("STUDIO_TEST_POSTGRES")!;
        string schema = "studio_message_route_" + Guid.NewGuid().ToString("N");
        using var admin = new NpgsqlConnection(connectionString);
        admin.Open();
        Sql(admin, $"create schema {schema}");

        using var source = NpgsqlDataSource.Create(new NpgsqlConnectionStringBuilder(connectionString) { SearchPath = schema }.ConnectionString);
        var database = new StudioDatabase(source);
        try
        {
            await database.InitializeAsync();
            await new StudioFeatureDatabase(source).InitializeAsync();
            await new StudioMessagingDatabase(source).InitializeAsync();

            IConfiguration configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string,string?>
                {
                    ["Messaging:PersonAddressDomain"] = "bryanstudiosltd.com"
                })
                .Build();
            var profiles = new UserProfileStore(database);
            var addresses = new StudioMessageAddressBook(database,profiles,configuration);
            var workspaces = new WorkspaceStore(database);
            var router = new ConfiguredEmailRouter(workspaces,addresses);
            var studio = new StudioStore(database,workspaces);

            ClaimsPrincipal fitzroy = User("fwright","Fitzroy Wright","directory@example.invalid");
            StudioUserProfile person = profiles.GetOrCreate(fitzroy);
            StudioMessageAddress personAddress = addresses.EnsurePerson(person);
            Assert.Equal("fwright@bryanstudiosltd.com",personAddress.Address);

            EmailRouteResult personRoute = router.Route(Email("p1","client@example.net",[personAddress.Address],"Person hello"));
            Assert.True(personRoute.IsRouted);
            Assert.Equal("Person",personRoute.DestinationType);
            Assert.Equal("fwright",personRoute.PersonUserName);
            Assert.Equal("Fitzroy Wright",personRoute.UserFacingContext);

            StudioWorkspaceDefinition workspace = workspaces.Create(new SaveWorkspaceRequest(
                "Weddings","weddings@bryanstudiosltd.com","weddings",true,1));
            EmailRouteResult workspaceRoute = router.Route(Email("w1","client@example.net",["weddings@bryanstudiosltd.com"],"Workspace hello"));
            Assert.True(workspaceRoute.IsRouted);
            Assert.Equal("Workspace",workspaceRoute.DestinationType);
            Assert.Equal(workspace.Id,workspaceRoute.WorkspaceId);
            Assert.Equal("Weddings",workspaceRoute.UserFacingContext);

            StudioConversation jobConversation = studio.ReceiveInteraction(new IncomingInteractionRequest(
                workspace.Id,null,"Campbell Family","campbell@example.net","Campbell Wedding","Initial enquiry","Email"));
            StudioJob job = studio.CreateJob(jobConversation.Id,new CreateJobRequest("Campbell Wedding",null));
            string jobAddress = addresses.JobAddress(job.Id);
            EmailRouteResult jobRoute = router.Route(Email("j1","campbell@example.net",[jobAddress],"Job hello"));
            Assert.True(jobRoute.IsRouted);
            Assert.Equal("Job",jobRoute.DestinationType);
            Assert.Equal(job.Id,jobRoute.DestinationId);
            Assert.Equal("Campbell Wedding",jobRoute.UserFacingContext);

            Assert.True(StudioMessageAddressBook.IsExternalEmail("outside@example.net"));
            Assert.Null(addresses.ResolveAddress("outside@example.net"));

            Guid externalConversation = Guid.NewGuid();
            using (NpgsqlConnection connection = database.OpenConnection())
            {
                Sql(connection,"insert into studio_conversations(id,workspace_id,subject,contact_name,contact_address,channel,is_closed,job_id,external_thread_id,created_at_utc) values($1,$2,'Outside test','Outside','outside@example.net','Email',false,null,null,now())",
                    externalConversation,personAddress.WorkspaceId!.Value);
            }
            StudioConversation external = studio.GetConversation(externalConversation)!;
            var outbox = new OutboundEmailStore(database,workspaces);
            OutboundEmail queued = outbox.QueuePerson(external,"Hello outside",personAddress.Address,"fwright",Guid.NewGuid());
            Assert.Equal("outside@example.net",queued.ToAddress);
            Assert.Equal("Person",queued.SendingIdentityType);
            Assert.Equal(personAddress.Address,queued.FromAddress);
            Assert.Contains(StudioMessageAddressBook.ReferenceToken(externalConversation),queued.Subject);

            EmailRouteResult reply = router.Route(Email(
                "reply1",
                "outside@example.net",
                ["AegisHub@bryanstudiosltd.com"],
                $"Re: [{StudioMessageAddressBook.ReferenceToken(externalConversation)}] Outside test"));
            Assert.True(reply.IsRouted);
            Assert.Equal("Person",reply.DestinationType);
            Assert.Equal("fwright",reply.PersonUserName);
        }
        finally
        {
            Sql(admin,$"drop schema {schema} cascade");
        }
    }

    [Fact]
    public void NotificationContextUsesConversationDestinationNotInfrastructureName()
    {
        EmailRouteResult person = new(true,Guid.NewGuid(),"Personal mailbox — Fitzroy",null,null,"Person",Guid.NewGuid(),"fwright","Fitzroy Wright");
        EmailRouteResult workspace = new(true,Guid.NewGuid(),"Weddings",null,null,"Workspace",Guid.NewGuid(),null,"Weddings");
        EmailRouteResult job = new(true,Guid.NewGuid(),"Weddings",null,null,"Job",Guid.NewGuid(),null,"Campbell Wedding");

        Assert.Equal("Fitzroy Wright",person.UserFacingContext);
        Assert.Equal("Weddings",workspace.UserFacingContext);
        Assert.Equal("Campbell Wedding",job.UserFacingContext);
        Assert.DoesNotContain("Hub",person.UserFacingContext,StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Hub",job.UserFacingContext,StringComparison.OrdinalIgnoreCase);
    }

    private static InboundEmail Email(string id,string from,IReadOnlyCollection<string> to,string subject)
        => new(id,null,"Lisa",from,to,[],new Dictionary<string,string>(),subject,"Body",DateTimeOffset.UtcNow);

    private static ClaimsPrincipal User(string login,string display,string email)
        => new(new ClaimsIdentity(
        [
            new Claim("login_name",login),
            new Claim(ClaimTypes.NameIdentifier,login),
            new Claim(ClaimTypes.Name,display),
            new Claim(ClaimTypes.Email,email)
        ],"Test"));

    private static void Sql(NpgsqlConnection connection,string sql,params object[] values)
    {
        using NpgsqlCommand command=connection.CreateCommand();
        command.CommandText=sql;
        foreach(object value in values)command.Parameters.AddWithValue(value);
        command.ExecuteNonQuery();
    }
}

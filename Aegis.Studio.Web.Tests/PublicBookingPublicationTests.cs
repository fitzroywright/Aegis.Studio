using System.Net;
using System.Text;
using Aegis.Studio.Web.Bookings;
using Aegis.Studio.Web.Operations;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace Aegis.Studio.Web.Tests;

public sealed class PublicBookingPublicationTests
{
    private static PublicBookingSessionType Session(string id,bool published)=>new(
        id,id,"Portrait session",60,10,15,1250000,250000,"JMD",["BK","BE"],
        new Dictionary<string,int>{{"Photographer",1}},true,published,Category:"Portrait");

    [StudioPostgresFact]
    public void OnlineBookingSwitchPersistsOnlyPublicationAndChangesPublishedCatalog()
    {
        using var context=new IntegratedWorkflowTests.WorkflowContext();
        var store=new StudioPublicBookingCatalogStore(context.Database);store.EnsureSchema();
        var original=store.Save([Session("portrait",true),Session("graduation",false)]);
        Assert.Single(store.Get(true).SessionTypes);
        store.SetPublication("portrait",new(false,original.Revision));
        var disabled=store.Get();Assert.Empty(store.Get(true).SessionTypes);
        var row=disabled.SessionTypes.Single(x=>x.Id=="portrait");
        Assert.False(row.IsPublished);Assert.Equal(1250000,row.PriceMinor);Assert.Equal("Portrait",row.Category);
        Assert.Equal(10,row.SetupBufferMinutes);Assert.Equal(1,row.ResourceRequirements["Photographer"]);
        Assert.Equal(new[]{"BK","BE"},row.Locations);
        Assert.False(disabled.SessionTypes.Single(x=>x.Id=="graduation").IsPublished);
        store.SetPublication("portrait",new(true,disabled.Revision));
        Assert.Single(store.Get(true).SessionTypes);
        var enabled=store.Get();store.SetPublication("portrait",new(true,enabled.Revision));
        Assert.Equal(enabled.Revision,store.Get().Revision);
        Assert.Throws<InvalidOperationException>(()=>store.SetPublication("portrait",new(false,original.Revision)));
        Assert.True(store.Get().SessionTypes.Single(x=>x.Id=="portrait").IsPublished);
    }

    [StudioPostgresFact]
    public void StaleCatalogAndHoursWritesCannotOverwriteAPublicationChange()
    {
        using var context=new IntegratedWorkflowTests.WorkflowContext();
        var store=new StudioPublicBookingCatalogStore(context.Database);store.EnsureSchema();
        var before=store.Save([Session("portrait",false)]);
        store.SetPublication("portrait",new(true,before.Revision));
        Assert.Throws<InvalidOperationException>(()=>store.Save(before.SessionTypes,before.Revision));
        Assert.Throws<InvalidOperationException>(()=>store.SaveHours([new("BK",4,"09:00","17:00",30)],before.Revision));
        Assert.Empty(store.GetHours());Assert.True(store.Get().SessionTypes.Single().IsPublished);
        var current=store.Get();store.Save(current.SessionTypes.Select(row=>row with{IsActive=false}).ToArray(),current.Revision);
        Assert.Empty(store.Get(true).SessionTypes);
        Assert.Throws<InvalidOperationException>(()=>store.SetPublication("portrait",new(true,store.Get().Revision)));
        Assert.Throws<KeyNotFoundException>(()=>store.SetPublication("missing",new(true,store.Get().Revision)));
    }
    [Fact]
    public async Task ConfiguredBookingPublisherCanReachOnlyBookingEndpointsInIsolatedDemo()
    {
        var handler=new BookingSiteHandler();
        IConfiguration configuration=new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string,string?>
        {
            [DemoIntegrationIsolation.ConfigurationKey]="true",
            ["PublicSite:BaseUrl"]="https://api.example.test/",
            ["PublicSite:IntegrationToken"]=new string('s',40)
        }).Build();

        using var site=new PublicBookingSiteClient(configuration,handler);
        JsonElement catalog=await site.PutCatalog(new{sessionTypes=Array.Empty<object>()},"studio-catalog-7",default);
        JsonElement availability=await site.PutAvailability(new{slots=Array.Empty<object>()},"availability-test",default);

        Assert.Equal(9,catalog.GetProperty("revision").GetInt32());
        Assert.Equal(9,availability.GetProperty("revision").GetInt32());
        Assert.Equal(
        [
            ("/api/v1/admin/booking/catalog","studio-catalog-7"),
            ("/api/v1/admin/booking/availability","availability-test")
        ],handler.Requests.Select(x=>(x.Path,x.IdempotencyKey)).ToArray());
        Assert.All(handler.Requests,request=>
        {
            Assert.Equal(HttpMethod.Put,request.Method);
            Assert.Equal("Bearer",request.AuthenticationScheme);
            Assert.Equal(40,request.TokenLength);
        });
    }

    private sealed record BookingSiteRequest(HttpMethod Method,string Path,string IdempotencyKey,string? AuthenticationScheme,int TokenLength);

    private sealed class BookingSiteHandler : HttpMessageHandler
    {
        internal List<BookingSiteRequest> Requests {get;}=[];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken cancellationToken)
        {
            Requests.Add(new(
                request.Method,
                request.RequestUri!.AbsolutePath,
                request.Headers.GetValues("Idempotency-Key").Single(),
                request.Headers.Authorization?.Scheme,
                request.Headers.Authorization?.Parameter?.Length??0));
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content=new StringContent("{\"revision\":9}",Encoding.UTF8,"application/json")
            });
        }
    }

}

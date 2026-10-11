using System.Text;
using System.Text.Json;
using Aegis.Studio.Web.PublicApi;
using Microsoft.AspNetCore.Http;
using Aegis.Studio.Web.Operations;
using Xunit;
namespace Aegis.Studio.Web.Tests;

public sealed class PublicV1ContractTests
{
    [Fact]
    public void SignedRequestsBindBodyQueryMethodAndIdempotencyKey()
    {
        var body=Encoding.UTF8.GetBytes("{\"clientName\":\"Morgan\"}");
        string canonical=PublicV1Signing.Canonical("POST","/api/public/v1/bookings?source=web","12345","nonce",body,"order-1");
        string signature=PublicV1Signing.Sign("test-secret",canonical);
        Assert.True(PublicV1Signing.Verify("test-secret",canonical,signature));
        Assert.False(PublicV1Signing.Verify("test-secret",canonical.Replace("order-1","order-2"),signature));
        Assert.False(PublicV1Signing.Verify("test-secret",canonical.Replace("source=web","source=other"),signature));
        Assert.False(PublicV1Signing.Verify("test-secret",PublicV1Signing.Canonical("POST","/api/public/v1/bookings?source=web","12345","nonce",Encoding.UTF8.GetBytes("{}"),"order-1"),signature));
        Assert.False(PublicV1Signing.Verify("test-secret",canonical,"malformed"));
    }
    [Fact]
    public void V1CannotRemoveExistingRoutesFieldsTypesOrEnumValues()
    {
        using var embedded=typeof(PublicV1Store).Assembly.GetManifestResourceStream("Aegis.Studio.Web.PublicApi.public-v1.openapi.json")!;
        using var current=JsonDocument.Parse(embedded);
        using var baseline=JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory,"Fixtures","public-v1.baseline.json")));
        foreach(var path in baseline.RootElement.GetProperty("paths").EnumerateObject())
        {
            Assert.True(current.RootElement.GetProperty("paths").TryGetProperty(path.Name,out var route));
            foreach(var method in path.Value.EnumerateObject()) Assert.True(route.TryGetProperty(method.Name,out _));
        }
        var schemas=current.RootElement.GetProperty("components").GetProperty("schemas");
        foreach(var schema in baseline.RootElement.GetProperty("components").GetProperty("schemas").EnumerateObject())
        {
            var latest=schemas.GetProperty(schema.Name);
            foreach(var field in schema.Value.GetProperty("properties").EnumerateObject())
            {
                var actual=latest.GetProperty("properties").GetProperty(field.Name);
                if(field.Value.TryGetProperty("type",out var type)) Assert.Equal(type.GetRawText(),actual.GetProperty("type").GetRawText());
                if(field.Value.TryGetProperty("enum",out var values)) foreach(var value in values.EnumerateArray())
                    Assert.Contains(actual.GetProperty("enum").EnumerateArray(),x=>x.GetString()==value.GetString());
            }
        }
    }
    [Fact]
    public void ReceiptOnlyExposesStableExternalContract()
    {
        var value=JsonSerializer.SerializeToElement(new PublicBookingReceiptV1(Guid.NewGuid(),"received",100,"JMD"),PublicV1Store.Json);
        Assert.Equal(new[]{"currency","reference","requiredDeposit","status"},value.EnumerateObject().Select(p=>p.Name).Order().ToArray());
        Assert.Throws<JsonException>(()=>JsonSerializer.Deserialize<PublicSelectionV1>("{}",PublicV1Store.Json));
        Assert.Equal("confirmed",PublicV1Store.ExternalStatus("Needs Assignment"));
        Assert.Equal("cancelled",PublicV1Store.ExternalStatus("Cancelled"));
    }
    [Fact]
    public void V1HasAnIndependentRateLimit()
    {
        var context=new DefaultHttpContext();context.Request.Path="/api/public/v1/bookings";context.Request.Method="POST";
        Assert.Equal("public-v1",ProductionRequestPolicy.GetRateLimitPolicy(context.Request)!.Scope);
    }
}

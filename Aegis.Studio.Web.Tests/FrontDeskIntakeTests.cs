using Aegis.Studio.Domain.Bookings;
using Aegis.Studio.Web.FrontDesk;
using Npgsql;
using Xunit;

namespace Aegis.Studio.Web.Tests;

public sealed class FrontDeskIntakeTests
{
    [Theory]
    [InlineData("Website", BookingSource.Website)]
    [InlineData("Walk-In", BookingSource.WalkIn)]
    [InlineData("Phone", BookingSource.Phone)]
    [InlineData("Email", BookingSource.Email)]
    [InlineData("Staff Entry", BookingSource.StaffEntry)]
    public void ExactlySupportedBusinessSourcesAreSelectable(string source, BookingSource expected)
    {
        Assert.Equal(source, BookingSources.RequireSelectable(source));
        Assert.Equal(expected, BookingSources.Parse(source));
        Assert.Equal(5, BookingSources.Selectable.Count);
        Assert.DoesNotContain("Unknown", BookingSources.Selectable);
        Assert.Throws<ArgumentException>(() => BookingSources.RequireSelectable("Unknown"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("legacy-unclassified")]
    public void HistoricalMissingOrUnsupportedSourceIsNeverInvented(string? source)
    {
        Assert.Equal("Unknown",BookingSources.Normalize(source));
        Assert.Equal(BookingSource.Unknown,BookingSources.Parse(source));
        Assert.Equal("Unknown",FrontDeskIntakeStore.Source(source,null));
        Assert.Equal("Unknown",FrontDeskIntakeStore.Source(source,"[]"));
        Assert.Equal("Unknown",FrontDeskIntakeStore.Source(source,"{\"bookingSource\":5}"));
    }

    [Theory]
    [InlineData("Online","Website")]
    [InlineData("WebsiteV1","Website")]
    [InlineData("Web site","Website")]
    [InlineData("WalkIn","Walk-In")]
    [InlineData("Manual","Staff Entry")]
    public void ReliableHistoricalAliasesUseTheBusinessLabels(string stored,string expected)
        =>Assert.Equal(expected,BookingSources.Normalize(stored));

    [Fact]
    public void HistoricalProviderNamespaceIsNotMistakenForBusinessSource()
    {
        Assert.Equal("Unknown",FrontDeskIntakeStore.Source("Email","{\"bookingSource\":\"FrontDesk\"}"));
        Assert.Equal("Unknown",FrontDeskIntakeStore.Source("Email","{}"));
        Assert.Equal("Email",FrontDeskIntakeStore.Source("Email","{\"conversationId\":\"original-enquiry\",\"bookingSource\":\"FrontDesk\"}"));
        Assert.Equal("Staff Entry",FrontDeskIntakeStore.Source("Email","{\"source\":\"Staff Entry\",\"bookingSource\":\"FrontDesk\"}"));
    }

    [Theory]
    [InlineData("today","2026-11-03","2026-11-03")]
    [InlineData("yesterday","2026-11-02","2026-11-02")]
    [InlineData("7days","2026-10-28","2026-11-03")]
    [InlineData("30days","2026-10-05","2026-11-03")]
    [InlineData("month","2026-11-01","2026-11-30")]
    public void ReceivedPresetsUseStudioLocalDate(string preset,string from,string to)
    {
        var zone=TimeZoneInfo.FindSystemTimeZoneById("America/Jamaica");
        var range=FrontDeskReceivedRange.Resolve(preset,null,null,DateTimeOffset.Parse("2026-11-04T02:00:00Z"),zone);
        Assert.Equal(DateOnly.Parse(from),range.From);Assert.Equal(DateOnly.Parse(to),range.To);
    }

    [Fact]
    public void CustomDatesIncludeBothLocalDaysAndRespectDaylightSaving()
    {
        var zone=TimeZoneInfo.FindSystemTimeZoneById("America/New_York");
        var day=DateOnly.Parse("2026-11-01");
        var range=FrontDeskReceivedRange.Resolve("custom",day,day,DateTimeOffset.UtcNow,zone);
        Assert.False(range.Includes(DateTimeOffset.Parse("2026-11-01T03:59:59Z"),zone));
        Assert.True(range.Includes(DateTimeOffset.Parse("2026-11-01T04:00:00Z"),zone));
        Assert.True(range.Includes(DateTimeOffset.Parse("2026-11-02T04:59:59Z"),zone));
        Assert.False(range.Includes(DateTimeOffset.Parse("2026-11-02T05:00:00Z"),zone));
        Assert.False(range.Includes(null,zone));
        Assert.Throws<ArgumentException>(()=>FrontDeskReceivedRange.Resolve("custom",day.AddDays(1),day,DateTimeOffset.UtcNow,zone));
    }

    [StudioPostgresFact]
    public async Task NewBookingsPersistAllFiveSourcesAndDefaultStaffEntry()
    {
        await using var fixture=await FrontDeskBookingRequestTests.DatabaseFixture.Create();
        var store=new FrontDeskBookingRequestStore(fixture.Database);
        foreach(var source in BookingSources.Selectable)
        {
            var saved=store.Create(Request(fixture.Workspace) with{Source=source},"staff");
            Assert.Equal(source,saved.Request.Source);
            var metadata=new FrontDeskIntakeStore(fixture.Database).List("any",null,null,source,TimeZoneInfo.Utc,DateTimeOffset.UtcNow);
            Assert.Contains(metadata,x=>x.JobId==saved.JobId&&x.Source==source&&x.ReceivedAtUtc.HasValue);
        }
        var manual=store.Create(Request(fixture.Workspace),"staff");
        Assert.Equal("Staff Entry",manual.Request.Source);
        var website=store.Create(Request(fixture.Workspace) with{BookingSource="WebsiteV1"},"website");
        Assert.Equal("Website",website.Request.Source);
        Assert.Throws<ArgumentException>(()=>store.Create(Request(fixture.Workspace) with{Source="Unknown"},"staff"));
    }

    [StudioPostgresFact]
    public async Task ConversionKeepsOriginalSourceAndReceivedDateAndCombinesFilters()
    {
        await using var fixture=await FrontDeskBookingRequestTests.DatabaseFixture.Create();
        Guid enquiry=Guid.NewGuid();var received=DateTimeOffset.Parse("2020-05-03T04:30:00Z");
        using(var c=fixture.Database.OpenConnection())using(var q=new NpgsqlCommand("insert into studio_conversations(id,workspace_id,subject,contact_name,contact_address,channel,is_closed,created_at_utc) values($1,$2,'Original enquiry','Client','client@example.test','Phone',false,$3)",c))
        {q.Parameters.AddWithValue(enquiry);q.Parameters.AddWithValue(fixture.Workspace);q.Parameters.AddWithValue(received);q.ExecuteNonQuery();}
        var store=new FrontDeskBookingRequestStore(fixture.Database);
        var saved=store.Create(Request(fixture.Workspace) with{ConversationId=enquiry,Source="Website"},"staff");
        Assert.Equal("Phone",saved.Request.Source);
        var metadata=new FrontDeskIntakeStore(fixture.Database);var zone=TimeZoneInfo.FindSystemTimeZoneById("America/Jamaica");
        var day=DateOnly.Parse("2020-05-02");
        var results=metadata.List("custom",day,day,"Phone",zone,DateTimeOffset.UtcNow);
        Assert.Equal(received,Assert.Single(results).ReceivedAtUtc);
        Assert.Equal(saved.JobId,results[0].JobId);
        Assert.Empty(metadata.List("custom",day,day,"Website",zone,DateTimeOffset.UtcNow));
        Assert.Empty(metadata.List("today",null,null,"Phone",zone,DateTimeOffset.UtcNow));
        Assert.Throws<ArgumentException>(()=>metadata.List("any",null,null,"Unknown",zone,DateTimeOffset.UtcNow));
    }

    [StudioPostgresFact]
    public async Task EmailUsesOriginalIncomingTimestampAndUnknownHistoryStaysUnknown()
    {
        await using var fixture=await FrontDeskBookingRequestTests.DatabaseFixture.Create();
        Guid email=Guid.NewGuid(),unknown=Guid.NewGuid();
        using(var c=fixture.Database.OpenConnection())
        {
            using(var q=new NpgsqlCommand("insert into studio_conversations(id,workspace_id,channel,is_closed,created_at_utc) values($1,$2,'Email',false,'2026-11-02T15:00:00Z'),($3,$2,'legacy-unclassified',false,null)",c))
            {q.Parameters.AddWithValue(email);q.Parameters.AddWithValue(fixture.Workspace);q.Parameters.AddWithValue(unknown);q.ExecuteNonQuery();}
            using(var q=new NpgsqlCommand("insert into studio_messages(id,conversation_id,visibility,created_at_utc) values($1,$2,'External','2020-02-03T09:00:00Z')",c))
            {q.Parameters.AddWithValue(Guid.NewGuid());q.Parameters.AddWithValue(email);q.ExecuteNonQuery();}
        }
        var records=new FrontDeskIntakeStore(fixture.Database).List("any",null,null,null,TimeZoneInfo.Utc,DateTimeOffset.UtcNow);
        Assert.Equal(DateTimeOffset.Parse("2020-02-03T09:00:00Z"),records[0].ReceivedAtUtc);
        Assert.Equal("Email",records[0].Source);Assert.Null(records[1].ReceivedAtUtc);Assert.Equal("Unknown",records[1].Source);
    }
    [StudioPostgresFact]
    public void ManualEnquiriesPersistExplicitSourcesAndDefaultStaffEntry()
    {
        using var context=new IntegratedWorkflowTests.WorkflowContext();
        var store=new Aegis.Studio.Web.Operations.StudioStore(context.Database,new Aegis.Studio.Web.Operations.WorkspaceStore(context.Database));
        foreach(var source in BookingSources.Selectable)
        {
            var enquiry=store.ReceiveInteraction(new(context.Workspace,null,"Client","client@example.test","Enquiry","Details",null,source));
            Assert.Equal(source,enquiry.Channel);
        }
        var direct=store.ReceiveInteraction(new(context.Workspace,null,"Client","client@example.test","Direct enquiry","Details",null));
        Assert.Equal("Staff Entry",direct.Channel);
    }

    private static PendingBookingRequest Request(Guid workspace)=>new(workspace,"Client","client@example.test","Booking",DateOnly.FromDateTime(DateTime.UtcNow.AddDays(10)),0,"JMD",null);
}

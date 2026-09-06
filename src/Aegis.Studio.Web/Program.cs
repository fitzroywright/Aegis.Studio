using Aegis.Studio.Web.Demo;

WebApplicationBuilder builder = WebApplication.CreateBuilder(args);

builder.Services.AddHealthChecks();
builder.Services.AddSingleton<DemoStore>();

WebApplication app = builder.Build();

app.UseDefaultFiles();
app.UseStaticFiles();

app.MapHealthChecks("/health");

app.MapGet("/api/workspaces", (DemoStore store) => Results.Ok(store.GetWorkspaces()));
app.MapGet("/api/conversations", (Guid? workspaceId, DemoStore store) => Results.Ok(store.GetConversations(workspaceId)));
app.MapGet("/api/conversations/{id:guid}", (Guid id, DemoStore store) =>
{
    DemoConversation? conversation = store.GetConversation(id);
    return conversation is null ? Results.NotFound() : Results.Ok(conversation);
});
app.MapPost("/api/interactions", (IncomingInteractionRequest request, DemoStore store) =>
{
    if (string.IsNullOrWhiteSpace(request.ContactName) ||
        string.IsNullOrWhiteSpace(request.ContactAddress) ||
        string.IsNullOrWhiteSpace(request.Subject) ||
        string.IsNullOrWhiteSpace(request.Body))
    {
        return Results.BadRequest(new { error = "Contact, address, subject and message are required." });
    }

    return Results.Created("/api/conversations", store.ReceiveInteraction(request));
});
app.MapPost("/api/conversations/{id:guid}/messages", (Guid id, AddMessageRequest request, DemoStore store) =>
{
    if (string.IsNullOrWhiteSpace(request.Body))
    {
        return Results.BadRequest(new { error = "Message body is required." });
    }

    DemoConversation? conversation = store.GetConversation(id);
    return conversation is null ? Results.NotFound() : Results.Ok(store.AddMessage(id, request));
});
app.MapPost("/api/conversations/{id:guid}/jobs", (Guid id, CreateJobRequest request, DemoStore store) =>
{
    if (string.IsNullOrWhiteSpace(request.Title))
    {
        return Results.BadRequest(new { error = "Job title is required." });
    }

    DemoConversation? conversation = store.GetConversation(id);
    return conversation is null ? Results.NotFound() : Results.Ok(store.CreateJob(id, request));
});

app.MapGet("/api/jobs", (DemoStore store) => Results.Ok(store.GetJobs()));
app.MapGet("/api/jobs/{id:guid}", (Guid id, DemoStore store) =>
{
    DemoJob? job = store.GetJob(id);
    return job is null ? Results.NotFound() : Results.Ok(job);
});
app.MapPut("/api/jobs/{id:guid}", (Guid id, UpdateJobRequest request, DemoStore store) =>
{
    DemoJob? job = store.GetJob(id);
    return job is null ? Results.NotFound() : Results.Ok(store.UpdateJob(id, request));
});
app.MapPost("/api/jobs/{id:guid}/financial-events", (Guid id, AddFinancialEventRequest request, DemoStore store) =>
{
    if (string.IsNullOrWhiteSpace(request.Kind) || request.Amount == 0)
    {
        return Results.BadRequest(new { error = "Financial event type and non-zero amount are required." });
    }

    DemoJob? job = store.GetJob(id);
    return job is null ? Results.NotFound() : Results.Ok(store.AddFinancialEvent(id, request));
});
app.MapGet("/api/accounts/summary", (DemoStore store) => Results.Ok(store.GetAccountsSummary()));

app.MapFallbackToFile("index.html");

app.Run();

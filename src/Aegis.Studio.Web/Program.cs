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

app.MapFallbackToFile("index.html");

app.Run();

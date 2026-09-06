WebApplicationBuilder builder = WebApplication.CreateBuilder(args);

builder.Services.AddHealthChecks();

WebApplication app = builder.Build();

app.MapGet("/", () => Results.Ok(new
{
    product = "Aegis.Studio",
    purpose = "Bryan's Studio operations platform"
}));
app.MapHealthChecks("/health");

app.Run();

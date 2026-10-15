using Store.Api;
using Store.Api.Endpoints;
using Store.Application;
using Store.Infrastructure;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddOpenApi();
builder.Services.AddApplication(cfg =>
{
    LocalizedMessages.Apply(cfg.Messages);
    cfg.AddPersistenceTransactions(); // every command in a database transaction
});
builder.Services.AddInfrastructure(builder.Configuration.GetConnectionString("Store") ?? "Data Source=store.db");

var app = builder.Build();

await app.Services.InitializeDatabaseAsync(reset: app.Configuration.GetValue("Store:ResetDatabaseOnStart", false));

// Culture per request from Accept-Language: DotCourier's messages follow it (see LocalizedMessages).
app.UseRequestLocalization(o => o
    .SetDefaultCulture("en-US")
    .AddSupportedCultures("en-US", "pt-BR")
    .AddSupportedUICultures("en-US", "pt-BR"));

app.MapOpenApi();
app.UseSwaggerUI(o =>
{
    o.SwaggerEndpoint("/openapi/v1.json", "Store API v1");
    o.RoutePrefix = "swagger";
    o.DocumentTitle = "Store API — DotCourier";
});
app.MapGet("/", () => Results.Redirect("/swagger")).ExcludeFromDescription();

app.MapProductEndpoints();
app.MapOrderEndpoints();
app.MapReportEndpoints();
app.MapSimulationEndpoints();

app.Run();

/// <summary>Exposed for integration tests (WebApplicationFactory).</summary>
public partial class Program;

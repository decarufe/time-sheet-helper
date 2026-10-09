using Microsoft.EntityFrameworkCore;
using TimeSheetHelper.App;
using TimeSheetHelper.App.Components;
using TimeSheetHelper.Collectors.Microsoft365;
using TimeSheetHelper.Collectors.Windows;
using TimeSheetHelper.Storage;

using var singleInstance = new Mutex(initiallyOwned: true, @"Local\TimeSheetHelper", out var isFirstInstance);
if (!isFirstInstance)
    return;

// Content root = exe folder, so appsettings and wwwroot are found whatever the working directory (startup shortcut, installer).
var builder = WebApplication.CreateBuilder(new WebApplicationOptions { Args = args, ContentRootPath = AppContext.BaseDirectory });
// Per-machine settings written by the installer (Microsoft 365 app registration, Azure DevOps organizations, ...).
builder.Configuration.AddJsonFile("appsettings.Local.json", optional: true, reloadOnChange: true);

builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

builder.Services.AddTimeSheetStorage(DataPaths.DatabaseConnectionString);
builder.Services.AddWindowsCollectors(builder.Configuration);
builder.Services.AddMicrosoft365Collectors(builder.Configuration);
builder.Services.AddSingleton<TrayIcon>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<TrayIcon>());

var app = builder.Build();

await using (var db = await app.Services.GetRequiredService<IDbContextFactory<TimeSheetDbContext>>().CreateDbContextAsync())
{
    await db.Database.MigrateAsync();
}

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
}
app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);

app.UseAntiforgery();

app.MapIngestEndpoints();
app.MapStaticAssets();
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.Run();

using Microsoft.EntityFrameworkCore;
using TimeSheetHelper.App;
using TimeSheetHelper.App.Components;
using TimeSheetHelper.Collectors.Windows;
using TimeSheetHelper.Storage;

using var singleInstance = new Mutex(initiallyOwned: true, @"Local\TimeSheetHelper", out var isFirstInstance);
if (!isFirstInstance)
    return;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

builder.Services.AddTimeSheetStorage(DataPaths.DatabaseConnectionString);
builder.Services.AddWindowsCollectors(builder.Configuration);
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

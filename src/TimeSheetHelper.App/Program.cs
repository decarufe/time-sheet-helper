using TimeSheetHelper.App;
using TimeSheetHelper.App.Components;

if (Cli.IsCommand(args))
    return await Cli.RunAsync(args);

using var singleInstance = new Mutex(initiallyOwned: true, @"Local\TimeSheetHelper", out var isFirstInstance);
if (!isFirstInstance)
    return 0;

// Content root = exe folder, so appsettings and wwwroot are found whatever the working directory (startup shortcut, installer).
var builder = WebApplication.CreateBuilder(new WebApplicationOptions { Args = args, ContentRootPath = AppContext.BaseDirectory });
AppServices.AddConfiguration(builder.Configuration);

builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

builder.Services.AddTimeSheetHelper(builder.Configuration);
builder.Services.AddSingleton<TrayIcon>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<TrayIcon>());

var app = builder.Build();

await AppServices.MigrateDatabaseAsync(app.Services);

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
}
// The "not found" page is for the UI only: re-executing an API error would turn a 401 into an antiforgery 400.
app.UseWhen(
    context => !context.Request.Path.StartsWithSegments("/ingest"),
    ui => ui.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true));

app.UseAntiforgery();

app.MapIngestEndpoints();
app.MapStaticAssets();
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

await app.RunAsync();
return 0;

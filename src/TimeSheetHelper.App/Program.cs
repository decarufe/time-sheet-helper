using TimeSheetHelper.App;
using TimeSheetHelper.App.Components;
using TimeSheetHelper.App.Services;
using TimeSheetHelper.App.Sync;
using TimeSheetHelper.Storage;

if (Cli.IsCommand(args))
    return await Cli.RunAsync(args);

using var singleInstance = new Mutex(initiallyOwned: true, SingleInstanceName(), out var isFirstInstance);
if (!isFirstInstance)
    return 0;

// Content root = exe folder, so appsettings and wwwroot are found whatever the working directory (startup shortcut, installer).
var builder = WebApplication.CreateBuilder(new WebApplicationOptions { Args = args, ContentRootPath = AppContext.BaseDirectory });
AppServices.AddConfiguration(builder.Configuration);

builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

builder.Services.AddTimeSheetHelper(builder.Configuration);
builder.Services.Configure<SyncOptions>(builder.Configuration.GetSection(SyncOptions.SectionName));
builder.Services.AddSingleton<SyncScheduler>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<SyncScheduler>());
builder.Services.AddSingleton<ForecastRunner>();
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
    context => !context.Request.Path.StartsWithSegments("/ingest") && !context.Request.Path.StartsWithSegments(LocalFolders.FillScreenshotsRoute),
    ui => ui.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true));

app.UseAntiforgery();

app.MapIngestEndpoints();
app.MapForecastScreenshots();
app.MapStaticAssets();
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

await app.RunAsync();
return 0;

// A relocated data folder (TIMESHEETHELPER_DATA_DIR) is a separate instance: it may run next to the normal one.
static string SingleInstanceName() =>
    Environment.GetEnvironmentVariable(DataPaths.OverrideVariable) is { Length: > 0 } dir
        ? $@"Local\TimeSheetHelper-{Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(Path.GetFullPath(dir).ToLowerInvariant())))[..16]}"
        : @"Local\TimeSheetHelper";

using Microsoft.Extensions.Options;
using TimeSheetHelper.App;
using TimeSheetHelper.App.Components;
using TimeSheetHelper.App.Components.Shared;
using TimeSheetHelper.App.Plugins;
using TimeSheetHelper.App.Services;
using TimeSheetHelper.App.Sync;
using TimeSheetHelper.App.Updates;
using TimeSheetHelper.Core;
using TimeSheetHelper.Core.Activities;
using TimeSheetHelper.Storage;

if (Cli.IsCommand(args))
    return await Cli.RunAsync(args);

using var singleInstance = new Mutex(initiallyOwned: true, SingleInstanceName(), out var isFirstInstance);
var openUi = args.Contains(UiLauncher.OpenArgument, StringComparer.OrdinalIgnoreCase);
if (!isFirstInstance)
{
    // Started again from the Start menu: the running application only has to show its page.
    if (openUi)
        UiLauncher.Open(UiLauncher.ConfiguredUrl(UiLauncher.ReadSettings()));
    return 0;
}

// Content root = exe folder, so appsettings and wwwroot are found whatever the working directory (startup shortcut, installer).
var builder = WebApplication.CreateBuilder(new WebApplicationOptions
{
    // "--open" is ours, not a configuration switch.
    Args = args.Where(a => !a.Equals(UiLauncher.OpenArgument, StringComparison.OrdinalIgnoreCase)).ToArray(),
    ContentRootPath = AppContext.BaseDirectory,
});
AppServices.AddConfiguration(builder.Configuration);

builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

builder.Services.AddTimeSheetHelper(builder.Configuration);
builder.Services.Configure<SyncOptions>(builder.Configuration.GetSection(SyncOptions.SectionName));
// Plugins first: they must be running before the scheduler asks them for collectors and before the tray reads their state.
builder.Services.AddHostedService(sp => sp.GetRequiredService<PluginHost>());
// Built by hand: the class has a second constructor (a fixed list of collectors) that would make injection ambiguous.
builder.Services.AddSingleton(sp => new SyncScheduler(
    sp.GetRequiredService<ICollectorSource>(),
    sp.GetRequiredService<IOptions<SyncOptions>>(),
    sp.GetRequiredService<TimeProvider>(),
    sp.GetRequiredService<ILogger<SyncScheduler>>()));
builder.Services.AddHostedService(sp => sp.GetRequiredService<SyncScheduler>());
builder.Services.AddSingleton<ForecastRunner>();
builder.Services.AddHostedService<ForecastRefresher>();
builder.Services.Configure<UpdateOptions>(builder.Configuration.GetSection(UpdateOptions.SectionName));
builder.Services.AddSingleton(sp => new UpdateService(
    new HttpClient { Timeout = TimeSpan.FromMinutes(5) },
    sp.GetRequiredService<IOptions<UpdateOptions>>(),
    sp.GetRequiredService<IHostApplicationLifetime>(),
    sp.GetRequiredService<ILogger<UpdateService>>()));
builder.Services.AddHostedService(sp => sp.GetRequiredService<UpdateService>());
builder.Services.AddSingleton<TrayIcon>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<TrayIcon>());

var app = builder.Build();

if (openUi)
    app.Lifetime.ApplicationStarted.Register(() => UiLauncher.Open(UiLauncher.ConfiguredUrl(app.Configuration)));

await AppServices.MigrateDatabaseAsync(app.Services);
TimesheetUi.Sources = app.Services.GetRequiredService<ISourceCatalog>();

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

// A relocated data folder (TIMESHEETHELPER_DATA_DIR) is a separate instance: it may run next to the normal one.
static string SingleInstanceName() =>
    Environment.GetEnvironmentVariable(DataPaths.OverrideVariable) is { Length: > 0 } dir
        ? $@"Local\TimeSheetHelper-{Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(Path.GetFullPath(dir).ToLowerInvariant())))[..16]}"
        : @"Local\TimeSheetHelper";

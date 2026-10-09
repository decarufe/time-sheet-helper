using System.Globalization;
using System.Runtime.InteropServices;
using TimeSheetHelper.Core.Collectors;
using TimeSheetHelper.Forecast;

namespace TimeSheetHelper.App;

/// <summary>
/// Command-line entry points. Running without arguments starts the tray app instead.
///   TimeSheetHelper forecast login | discover | sync-tasks
///   TimeSheetHelper sync [--week yyyy-MM-dd] [--source name]
/// </summary>
public static class Cli
{
    private const string Usage = """
        Utilisation :
          TimeSheetHelper                                   démarre l'application (icône de notification + UI)
          TimeSheetHelper forecast login                    connexion à Forecast dans Edge (session conservée)
          TimeSheetHelper forecast discover                 capture l'interface Forecast pour ajuster les sélecteurs
          TimeSheetHelper forecast sync-tasks               met à jour la liste locale des tâches Forecast
          TimeSheetHelper sync [--week AAAA-MM-JJ] [--source nom]
                                                            collecte les sources à la demande pour une semaine
        """;

    public static bool IsCommand(string[] args) => args.Length > 0 && !args[0].StartsWith("--", StringComparison.Ordinal);

    public static async Task<int> RunAsync(string[] args)
    {
        AttachToParentConsole();
        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { Args = [], ContentRootPath = AppContext.BaseDirectory });
        AppServices.AddConfiguration(builder.Configuration);
        builder.Services.AddTimeSheetHelper(builder.Configuration);
        builder.Logging.SetMinimumLevel(LogLevel.Warning);
        // Collectors report their progress at Information level: long syncs should not look frozen.
        builder.Logging.AddFilter("TimeSheetHelper", LogLevel.Information);
        builder.Logging.AddSimpleConsole(o => o.SingleLine = true);
        using var host = builder.Build();
        await AppServices.MigrateDatabaseAsync(host.Services);

        var forecast = host.Services.GetRequiredService<ForecastCommands>();
        return args switch
        {
            ["forecast", "login"] => await forecast.LoginAsync(Console.Out, cts.Token),
            ["forecast", "discover"] => await forecast.DiscoverAsync(Console.Out, cts.Token),
            ["forecast", "sync-tasks"] => await forecast.SyncTasksAsync(Console.Out, cts.Token),
            ["sync", .. var options] => await SyncAsync(host.Services, options, cts.Token),
            _ => PrintUsage(),
        };
    }

    private static async Task<int> SyncAsync(IServiceProvider services, string[] options, CancellationToken cancellationToken)
    {
        var monday = StartOfWeek(DateOnly.FromDateTime(DateTime.Today));
        string? source = null;
        for (var i = 0; i < options.Length - 1; i += 2)
        {
            switch (options[i])
            {
                case "--week" when DateOnly.TryParseExact(options[i + 1], "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var day):
                    monday = StartOfWeek(day);
                    break;
                case "--source":
                    source = options[i + 1];
                    break;
                default:
                    return PrintUsage();
            }
        }

        var fromUtc = monday.ToDateTime(TimeOnly.MinValue, DateTimeKind.Local).ToUniversalTime();
        var toUtc = monday.AddDays(7).ToDateTime(TimeOnly.MinValue, DateTimeKind.Local).ToUniversalTime();
        var collectors = services.GetServices<ICollector>()
            .Where(c => source is null || c.Name.Equals(source, StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (collectors.Count == 0)
        {
            Console.Error.WriteLine($"Source inconnue : {source}. Sources : {string.Join(", ", services.GetServices<ICollector>().Select(c => c.Name))}");
            return 1;
        }

        Console.WriteLine($"Semaine du {monday:yyyy-MM-dd}");
        var exitCode = 0;
        foreach (var collector in collectors)
        {
            var status = await collector.GetStatusAsync(cancellationToken);
            if (!status.IsConfigured)
            {
                Console.WriteLine($"  {collector.Name,-12} ignoré : {status.Message}");
                continue;
            }
            try
            {
                var result = await collector.SyncAsync(fromUtc, toUtc, cancellationToken);
                Console.WriteLine($"  {collector.Name,-12} {result.EventCount} événement(s)");
                foreach (var warning in result.Warnings)
                    Console.WriteLine($"  {"",-12} ⚠ {warning}");
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Console.WriteLine($"  {collector.Name,-12} erreur : {ex.Message}");
                exitCode = 1;
            }
        }
        return exitCode;
    }

    private static DateOnly StartOfWeek(DateOnly day) => day.AddDays(-(((int)day.DayOfWeek + 6) % 7));

    private static int PrintUsage()
    {
        Console.Error.WriteLine(Usage);
        return 2;
    }

    [DllImport("kernel32.dll")]
    private static extern bool AttachConsole(int processId);

    /// <summary>The app is a WinExe (no console window for the tray app); borrow the terminal it was started from.</summary>
    private static void AttachToParentConsole()
    {
        if (!AttachConsole(-1))
            return;
        Console.SetOut(new StreamWriter(Console.OpenStandardOutput()) { AutoFlush = true });
        Console.SetError(new StreamWriter(Console.OpenStandardError()) { AutoFlush = true });
    }
}

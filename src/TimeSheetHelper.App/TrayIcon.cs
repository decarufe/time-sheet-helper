using System.Diagnostics;
using TimeSheetHelper.App.Plugins;

namespace TimeSheetHelper.App;

/// <summary>Notification-area icon: open the UI, pause tracking, quit, weekly reminder. Runs its own STA message loop.</summary>
public sealed class TrayIcon(PluginHost plugins, IHostApplicationLifetime lifetime, IConfiguration configuration) : IHostedService
{
    // Identifiers declared by the window-tracking plugin shipped with the application.
    private const string FocusTrackerId = "focus-tracker";
    private const string TogglePauseAction = "toggle-pause";
    private const string AppIconResource = "TimeSheetHelper.App.app.ico";

    private Thread? _thread;
    private ApplicationContext? _context;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _thread = new Thread(Run) { IsBackground = true, Name = "TrayIcon" };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _context?.ExitThread();
        return Task.CompletedTask;
    }

    /// <summary>Opens the application in the default browser.</summary>
    public void OpenUi() => UiLauncher.Open(UiLauncher.ConfiguredUrl(configuration));

    private void Run()
    {
        using var appIcon = LoadAppIcon();
        using var icon = new NotifyIcon
        {
            Icon = appIcon ?? SystemIcons.Application,
            Text = "Time Sheet Helper",
            Visible = true,
            ContextMenuStrip = new ContextMenuStrip(),
        };

        // The pause entry only exists while the window-tracking plugin runs; its label follows the plugin's own state.
        var pause = new ToolStripMenuItem("Mettre la surveillance en pause") { Visible = false };
        pause.Click += (_, _) => TogglePause();

        icon.ContextMenuStrip.Items.Add("Ouvrir la feuille de temps", null, (_, _) => OpenUi());
        icon.ContextMenuStrip.Items.Add(pause);
        icon.ContextMenuStrip.Items.Add(new ToolStripSeparator());
        icon.ContextMenuStrip.Items.Add("Quitter", null, (_, _) => lifetime.StopApplication());
        icon.ContextMenuStrip.Opening += (_, _) => RefreshPauseItem(pause, icon);
        icon.DoubleClick += (_, _) => OpenUi();

        // Weekly reminder: checked every minute on this STA thread (WinForms timer), clicking the balloon opens the UI.
        var reminder = new WeeklyReminder(configuration.GetSection(ReminderOptions.SectionName).Get<ReminderOptions>() ?? new());
        icon.BalloonTipClicked += (_, _) => OpenUi();
        using var reminderTimer = new System.Windows.Forms.Timer { Interval = 60_000 };
        reminderTimer.Tick += (_, _) =>
        {
            if (!reminder.IsDue(DateTime.Now))
                return;
            reminder.MarkShown(DateTime.Now);
            icon.ShowBalloonTip(15_000, "Time Sheet Helper", WeeklyReminder.Message, ToolTipIcon.Info);
        };
        reminderTimer.Start();

        _context = new ApplicationContext();
        Application.Run(_context);
        icon.Visible = false;
    }

    /// <summary>The application icon embedded in the assembly, at the size the tray uses; null when it cannot be loaded.</summary>
    internal static Icon? LoadAppIcon()
    {
        try
        {
            using var stream = typeof(TrayIcon).Assembly.GetManifestResourceStream(AppIconResource);
            return stream is null ? null : new Icon(stream, SystemInformation.SmallIconSize);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or IOException)
        {
            return null;
        }
    }

    private void RefreshPauseItem(ToolStripMenuItem item, NotifyIcon icon)
    {
        var status = plugins.GetStatusAsync(FocusTrackerId).GetAwaiter().GetResult();
        var action = status?.Actions?.FirstOrDefault(a => a.Id == TogglePauseAction);
        item.Visible = action is not null;
        if (action is not null)
            item.Text = action.Label;
        icon.Text = status?.Message == "En pause" ? "Time Sheet Helper (en pause)" : "Time Sheet Helper";
    }

    private void TogglePause()
    {
        _ = Task.Run(async () =>
        {
            try
            {
                await plugins.ExecuteActionAsync(FocusTrackerId, TogglePauseAction);
            }
            catch (InvalidOperationException)
            {
                // Plugin switched off since the menu opened: nothing to pause.
            }
        });
    }
}

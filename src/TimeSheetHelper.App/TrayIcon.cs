using System.Diagnostics;
using TimeSheetHelper.Collectors.Windows;

namespace TimeSheetHelper.App;

/// <summary>Notification-area icon: open the UI, pause tracking, quit, weekly reminder. Runs its own STA message loop.</summary>
public sealed class TrayIcon(FocusTracker tracker, IHostApplicationLifetime lifetime, IConfiguration configuration) : IHostedService
{
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

    public void OpenUi() =>
        Process.Start(new ProcessStartInfo(configuration["Urls"]!.Split(';')[0]) { UseShellExecute = true });

    private void Run()
    {
        using var icon = new NotifyIcon
        {
            Icon = SystemIcons.Application,
            Text = "Time Sheet Helper",
            Visible = true,
            ContextMenuStrip = new ContextMenuStrip(),
        };

        var pause = new ToolStripMenuItem("Mettre la surveillance en pause");
        pause.Click += (_, _) =>
        {
            tracker.SetPaused(!tracker.IsPaused);
            pause.Text = tracker.IsPaused ? "Reprendre la surveillance" : "Mettre la surveillance en pause";
            icon.Text = tracker.IsPaused ? "Time Sheet Helper (en pause)" : "Time Sheet Helper";
        };

        icon.ContextMenuStrip.Items.Add("Ouvrir la feuille de temps", null, (_, _) => OpenUi());
        icon.ContextMenuStrip.Items.Add(pause);
        icon.ContextMenuStrip.Items.Add(new ToolStripSeparator());
        icon.ContextMenuStrip.Items.Add("Quitter", null, (_, _) => lifetime.StopApplication());
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
}

using System.Diagnostics;
using System.Runtime.InteropServices;
using TimeSheetHelper.App.About;
using TimeSheetHelper.App.Plugins;
using TimeSheetHelper.App.Updates;

namespace TimeSheetHelper.App;

/// <summary>Notification-area icon: open the UI, pause tracking, update, about, quit, weekly reminder. Runs its own STA message loop.</summary>
public sealed class TrayIcon(PluginHost plugins, UpdateService updates, AboutService about, IHostApplicationLifetime lifetime, IConfiguration configuration) : IHostedService
{
    private const string BaseTooltip = "Time Sheet Helper";
    private const string CheckLabel = "Rechercher une mise à jour";
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
        var updateItem = new ToolStripMenuItem(CheckLabel);
        icon.ContextMenuStrip.Items.Add(updateItem);
        var aboutItem = new ToolStripMenuItem("À propos");
        icon.ContextMenuStrip.Items.Add(aboutItem);
        icon.ContextMenuStrip.Items.Add(new ToolStripSeparator());
        icon.ContextMenuStrip.Items.Add("Quitter", null, (_, _) => lifetime.StopApplication());
        icon.ContextMenuStrip.Opening += (_, _) => RefreshPauseItem(pause, icon);
        icon.DoubleClick += (_, _) => OpenUi();

        // Update badge, menu entry and balloons follow the update service; its events arrive on other threads.
        using var plainIcon = (Icon)(appIcon ?? SystemIcons.Application).Clone();
        using var badgedIcon = CreateBadgedIcon(plainIcon);
        using var sync = new Control();
        _ = sync.Handle;
        var aboutOpen = false;
        aboutItem.Click += async (_, _) =>
        {
            if (aboutOpen)
                return;
            aboutOpen = true;
            try
            {
                var info = await Task.Run(() => about.GetAsync());
                var text = AboutFormat.Summary(info, AboutFormat.UpdateStatus(updates.Phase, updates.Available?.Version, updates.Error));
                MessageBox.Show(text, "À propos de " + AboutFormat.AppName, MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                MessageBox.Show($"Impossible de lire les informations : {ex.Message}", "À propos de " + AboutFormat.AppName, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
            finally
            {
                aboutOpen = false;
            }
        };
        var manualCheck = false;
        AppVersion? notified = null;
        updateItem.Click += (_, _) =>
        {
            if (updates.Available is not null)
            {
                _ = updates.InstallAsync();
                return;
            }
            manualCheck = true;
            _ = updates.CheckAsync();
        };

        void RefreshUpdate()
        {
            var available = updates.Available;
            var phase = updates.Phase;
            updateItem.Enabled = phase is UpdatePhase.Idle or UpdatePhase.Failed;
            updateItem.Font = new Font(updateItem.Font, available is null ? FontStyle.Regular : FontStyle.Bold);
            updateItem.Text = phase switch
            {
                UpdatePhase.Checking => "Recherche de mise à jour…",
                UpdatePhase.Downloading => "Téléchargement de la mise à jour…",
                UpdatePhase.Installing => "Installation de la mise à jour…",
                _ => available is null ? CheckLabel : $"Mettre à jour vers {available.Version}",
            };
            icon.Icon = available is null || badgedIcon is null ? plainIcon : badgedIcon;
            icon.Text = available is null ? BaseTooltip : Truncate($"{BaseTooltip} — mise à jour {available.Version} disponible");

            if (phase is UpdatePhase.Checking or UpdatePhase.Downloading or UpdatePhase.Installing)
                return;
            if (available is not null && (manualCheck || available.Version != notified))
            {
                notified = available.Version;
                icon.ShowBalloonTip(15_000, "Time Sheet Helper", $"La version {available.Version} est disponible. Cliquez pour la mettre à jour.", ToolTipIcon.Info);
            }
            else if (manualCheck)
            {
                var message = phase == UpdatePhase.Failed ? updates.Error : "Vous utilisez déjà la dernière version.";
                icon.ShowBalloonTip(5_000, "Time Sheet Helper", message ?? "", phase == UpdatePhase.Failed ? ToolTipIcon.Warning : ToolTipIcon.Info);
            }
            manualCheck = false;
        }

        void OnUpdateChanged()
        {
            if (sync.IsHandleCreated && !sync.IsDisposed)
                sync.BeginInvoke(RefreshUpdate);
        }

        updates.Changed += OnUpdateChanged;
        RefreshUpdate();

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
        updates.Changed -= OnUpdateChanged;
        icon.Visible = false;
    }

    private static string Truncate(string text) => text.Length <= 63 ? text : text[..62] + "…";

    /// <summary>A copy of <paramref name="source"/> with a red dot in the top-right corner; null if it cannot be drawn.</summary>
    private static Icon? CreateBadgedIcon(Icon source)
    {
        IntPtr handle = IntPtr.Zero;
        try
        {
            using var bitmap = source.ToBitmap();
            using (var g = Graphics.FromImage(bitmap))
            {
                g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                var d = Math.Max(6, bitmap.Width * 5 / 10);
                var area = new Rectangle(bitmap.Width - d, 0, d - 1, d - 1);
                using var ring = new SolidBrush(Color.White);
                g.FillEllipse(ring, Rectangle.Inflate(area, 1, 1));
                using var dot = new SolidBrush(Color.FromArgb(0xE0, 0x25, 0x25));
                g.FillEllipse(dot, area);
            }
            handle = bitmap.GetHicon();
            using var temp = Icon.FromHandle(handle);
            return (Icon)temp.Clone();
        }
        catch (Exception ex) when (ex is ArgumentException or ExternalException)
        {
            return null;
        }
        finally
        {
            if (handle != IntPtr.Zero)
                DestroyIcon(handle);
        }
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyIcon(IntPtr handle);

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
        var tooltip = status?.Message == "En pause" ? BaseTooltip + " (en pause)" : BaseTooltip;
        icon.Text = updates.Available is { } available
            ? Truncate($"{tooltip} — mise à jour {available.Version} disponible")
            : tooltip;
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

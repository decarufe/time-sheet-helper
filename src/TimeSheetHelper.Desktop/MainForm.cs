using System.Diagnostics;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace TimeSheetHelper.Desktop;

/// <summary>The application's pages in a native window; closing it leaves the background application running.</summary>
internal sealed class MainForm : Form
{
    private readonly Uri _url;
    private readonly WebView2 _web = new() { Dock = DockStyle.Fill, Visible = false };
    private readonly Label _status = new() { Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleCenter, Font = new Font(SystemFonts.MessageBoxFont!.FontFamily, 11f) };
    private readonly Button _retry = new() { Text = "Réessayer", AutoSize = true, Visible = false, Anchor = AnchorStyles.None };

    public MainForm(Uri url)
    {
        _url = url;
        Text = "Time Sheet Helper";
        Icon = SystemIcons.Application;
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(1280, 800);
        MinimumSize = new Size(800, 500);

        var retryPanel = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 56, FlowDirection = FlowDirection.LeftToRight, WrapContents = false, Padding = new Padding(8) };
        retryPanel.Controls.Add(_retry);
        _retry.Click += async (_, _) => await StartAsync();

        Controls.Add(_web);
        Controls.Add(_status);
        Controls.Add(retryPanel);
        Shown += async (_, _) => await StartAsync();
    }

    /// <summary>Brings the window to the front (a second launch asks the first to do it).</summary>
    public void ShowAndActivate()
    {
        if (WindowState == FormWindowState.Minimized)
            WindowState = FormWindowState.Normal;
        Activate();
    }

    private async Task StartAsync()
    {
        _retry.Visible = false;
        ShowMessage("Démarrage de Time Sheet Helper…");
        try
        {
            await HostLocator.EnsureRunningAsync(_url, Path.Combine(AppContext.BaseDirectory, HostLocator.HostExeName), TimeSpan.FromSeconds(40), CancellationToken.None);
            await InitializeWebViewAsync();
        }
        catch (WebView2RuntimeNotFoundException)
        {
            // Windows 10 without the Edge WebView2 runtime: the same pages work in the default browser.
            OpenExternally(_url);
            Close();
        }
        catch (Exception ex) when (ex is FileNotFoundException or TimeoutException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            ShowMessage(ex.Message);
            _retry.Visible = true;
        }
    }

    private async Task InitializeWebViewAsync()
    {
        if (_web.CoreWebView2 is null)
        {
            // Same data folder as the application (TIMESHEETHELPER_DATA_DIR relocates both, e.g. for a throwaway run).
            var dataRoot = Environment.GetEnvironmentVariable("TIMESHEETHELPER_DATA_DIR") is { Length: > 0 } custom
                ? custom
                : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TimeSheetHelper");
            var userData = Path.Combine(dataRoot, "webview2");
            var environment = await CoreWebView2Environment.CreateAsync(userDataFolder: userData);
            await _web.EnsureCoreWebView2Async(environment);

            var core = _web.CoreWebView2!;
            core.Settings.AreBrowserAcceleratorKeysEnabled = true;
            core.Settings.IsStatusBarEnabled = false;
            core.Settings.IsZoomControlEnabled = true;
            core.DocumentTitleChanged += (_, _) => Text = string.IsNullOrWhiteSpace(core.DocumentTitle) ? "Time Sheet Helper" : core.DocumentTitle;
            core.NavigationStarting += (_, e) =>
            {
                if (!Uri.TryCreate(e.Uri, UriKind.Absolute, out var target) || HostLocator.IsSameOrigin(target, _url))
                    return;

                // Only the application's own pages are shown here; anything else goes to the real browser.
                e.Cancel = true;
                OpenExternally(target);
            };
            core.NewWindowRequested += (_, e) =>
            {
                e.Handled = true;
                if (!Uri.TryCreate(e.Uri, UriKind.Absolute, out var target))
                    return;
                if (HostLocator.IsSameOrigin(target, _url))
                    core.Navigate(target.AbsoluteUri);
                else
                    OpenExternally(target);
            };
            core.NavigationCompleted += (_, e) =>
            {
                if (e.IsSuccess)
                    return;
                _web.Visible = false;
                ShowMessage("La page n'a pas pu être chargée (" + e.WebErrorStatus + ").");
                _retry.Visible = true;
            };
        }

        _web.Visible = true;
        _status.Visible = false;
        _web.CoreWebView2!.Navigate(_url.AbsoluteUri);
    }

    private void ShowMessage(string message)
    {
        _status.Text = message;
        _status.Visible = true;
        _web.Visible = false;
    }

    private static void OpenExternally(Uri uri)
    {
        if (HostLocator.IsOpenableExternally(uri))
            Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
    }
}

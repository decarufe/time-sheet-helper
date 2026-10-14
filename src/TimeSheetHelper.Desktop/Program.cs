namespace TimeSheetHelper.Desktop;

internal static class Program
{
    private const string ShowEventName = @"Local\TimeSheetHelper.Desktop.Show";

    // WebView2 (like all WinForms controls) needs a single-threaded apartment: top-level statements cannot declare it.
    [STAThread]
    private static void Main(string[] args)
    {
        // One window: opening it again brings the existing one to the front.
        using var singleInstance = new Mutex(initiallyOwned: true, @"Local\TimeSheetHelper.Desktop", out var isFirst);
        if (!isFirst)
        {
            try
            {
                using var show = EventWaitHandle.OpenExisting(ShowEventName);
                show.Set();
            }
            catch (WaitHandleCannotBeOpenedException)
            {
                // The first instance is still starting up: it will show its window anyway.
            }

            return;
        }

        ApplicationConfiguration.Initialize();
        var url = HostLocator.ResolveUrl(AppContext.BaseDirectory, args);
        var form = new MainForm(url);

        using var showRequested = new EventWaitHandle(false, EventResetMode.AutoReset, ShowEventName);
        var listener = new Thread(() =>
        {
            while (showRequested.WaitOne())
            {
                if (form.IsDisposed)
                    return;
                form.BeginInvoke(form.ShowAndActivate);
            }
        }) { IsBackground = true, Name = "ShowRequests" };
        listener.Start();

        Application.Run(form);
    }
}

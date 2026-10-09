using TimeSheetHelper.Storage;

namespace TimeSheetHelper.Forecast;

public sealed class ForecastOptions
{
    public const string SectionName = "Forecast";

    public string BaseUrl { get; set; } = "https://app.forecast.it";

    /// <summary>
    /// Run the browser without a window. Must stay false for the first login (SSO needs a visible window);
    /// when true and the session is expired, the session reopens a headed window anyway.
    /// </summary>
    public bool Headless { get; set; }

    /// <summary>Delay in milliseconds between Playwright actions (useful to watch what the automation does).</summary>
    public float SlowMo { get; set; }

    /// <summary>Browser channel; Edge is always installed on the target machines, so no browser download is needed.</summary>
    public string Channel { get; set; } = "msedge";

    /// <summary>Persistent browser profile (cookies, SSO session). Defaults to %LOCALAPPDATA%\TimeSheetHelper\browser.</summary>
    public string? UserDataDir { get; set; }

    /// <summary>How long to wait for the user to complete the login in the headed window.</summary>
    public TimeSpan LoginTimeout { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>Default Playwright timeout for a single action/locator wait.</summary>
    public TimeSpan ActionTimeout { get; set; } = TimeSpan.FromSeconds(15);

    /// <summary>
    /// How hours are typed into the timesheet cells. UNVERIFIED: "1h 30m" is locale-independent and is the
    /// format Forecast displays durations in; switch to Decimal/DecimalComma if the discovery shows otherwise.
    /// </summary>
    public ForecastHourFormat HourFormat { get; set; } = ForecastHourFormat.HoursMinutes;

    /// <summary>Relative path of the timesheets page.</summary>
    public string TimesheetsPath { get; set; } = "/timesheets";

    /// <summary>Relative path of the "My work" page (tasks assigned to the user).</summary>
    public string MyWorkPath { get; set; } = "/my-work";

    internal string ResolvedUserDataDir => UserDataDir ?? Path.Combine(DataPaths.Root, "browser");

    internal string Url(string path) => BaseUrl.TrimEnd('/') + "/" + path.TrimStart('/');
}

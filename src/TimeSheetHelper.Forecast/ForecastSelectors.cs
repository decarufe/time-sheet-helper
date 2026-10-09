using System.Text.RegularExpressions;
using Microsoft.Playwright;

namespace TimeSheetHelper.Forecast;

/// <summary>
/// Every locator used against app.forecast.it, in one place.
///
/// VERIFIED selectors come from the "forecast discover" capture of 2026-10-09 (page /timesheets, English UI):
/// Forecast exposes stable <c>data-cy</c> attributes and indexed ids, which are preferred over generated CSS classes.
/// UNVERIFIED ones are still guesses; run "forecast discover" again and fix them from the saved files.
/// Safety rule: never use a page-wide combobox/textbox locator — the page has a person selector
/// (data-cy="timesheets-select-person") and the time entry dialog a role selector that must not be typed into.
/// </summary>
public static class ForecastSelectors
{
    // Playwright translates .NET regexes to JavaScript and only accepts IgnoreCase, Multiline and Singleline;
    // any other option (e.g. CultureInvariant) fails with "Unsupported RegularExpression flags".
    public const RegexOptions PlaywrightSupportedOptions = RegexOptions.IgnoreCase | RegexOptions.Multiline | RegexOptions.Singleline;

    private const RegexOptions Ci = RegexOptions.IgnoreCase;

    private static Regex Rx(string pattern) => new(pattern, Ci);

    private static ILocator DataCy(IPage page, string value) => page.Locator($"[data-cy='{value}']");

    // ---------------------------------------------------------------- Login / app shell

    /// <summary>UNVERIFIED. URL fragments of the Forecast login page and of the usual SSO providers.</summary>
    public static readonly Regex LoginUrl = Rx(
        @"/login|/signin|/sign-in|/sso|/auth|login\.microsoftonline\.com|accounts\.google\.com|okta\.com|onelogin\.com|auth0\.com");

    /// <summary>UNVERIFIED. Email/password fields of the Forecast login form.</summary>
    public static ILocator LoginForm(IPage page) =>
        page.Locator("input[type=email], input[type=password], input[name=email], input[autocomplete=username]");

    /// <summary>VERIFIED. Main navigation "My Work" button.</summary>
    public static ILocator MyWorkNavigation(IPage page) => DataCy(page, "primary-nav-my-work");

    /// <summary>VERIFIED. Elements that only exist once logged in.</summary>
    public static ILocator LoggedInMarker(IPage page) =>
        MyWorkNavigation(page).Or(TimesheetsPage(page));

    // ---------------------------------------------------------------- Timesheet page

    /// <summary>VERIFIED. Root of the "My Timesheets" page.</summary>
    public static ILocator TimesheetsPage(IPage page) => DataCy(page, "timesheets-page");

    /// <summary>VERIFIED. "Day" / "Week" segmented control: the week option.</summary>
    public static ILocator WeekViewToggle(IPage page) => DataCy(page, "week");

    /// <summary>VERIFIED. "Today" button of the date navigation.</summary>
    public static ILocator TodayButton(IPage page) =>
        page.GetByRole(AriaRole.Button, new() { Name = "Today", Exact = true });

    /// <summary>VERIFIED. Previous period arrow (aria name "chevronLeft", data-cy "undefined-back-button").</summary>
    public static ILocator PreviousWeekButton(IPage page) =>
        DataCy(page, "undefined-back-button").Or(page.GetByRole(AriaRole.Button, new() { Name = "chevronLeft", Exact = true }));

    /// <summary>VERIFIED. Next period arrow (aria name "chevronRight", data-cy "undefined-foward-button" — sic).</summary>
    public static ILocator NextWeekButton(IPage page) =>
        DataCy(page, "undefined-foward-button").Or(page.GetByRole(AriaRole.Button, new() { Name = "chevronRight", Exact = true }));

    /// <summary>VERIFIED. Tab listing the rows that already have time this week ("Timesheets (n)").</summary>
    public static ILocator TimesheetTab(IPage page) => DataCy(page, "timesheet-tab");

    /// <summary>VERIFIED. Tab listing the tasks assigned this week ("Find Tasks (n)").</summary>
    public static ILocator FindTasksTab(IPage page) => DataCy(page, "this-weeks-work-tab");

    /// <summary>VERIFIED. Tab for internal time (non-project).</summary>
    public static ILocator InternalTimeTab(IPage page) => DataCy(page, "internal-time-tab");

    /// <summary>VERIFIED. The 7 day cells (Sunday..Saturday) of the grid row with index <paramref name="rowIndex"/>.</summary>
    public static ILocator DayCells(IPage page, int rowIndex) =>
        page.Locator($"#hour-inputs-time-reg-{rowIndex} [data-cy='hours-input-popup-toggle']");

    /// <summary>VERIFIED. Every day cell of the grid (used by the discovery).</summary>
    public static ILocator AllDayCells(IPage page) => DataCy(page, "hours-input-popup-toggle");

    /// <summary>
    /// UNVERIFIED. Text field of the popup that opens when a day cell is clicked. Restricted to elements that appear in
    /// a popover/dialog layer so it can never match the person selector or the Nova chat box.
    /// </summary>
    public static ILocator HourPopupInput(IPage page) =>
        page.Locator("[data-radix-popper-content-wrapper] input, [role=dialog] input, [data-cy*='hours-input' i] input, input[data-cy*='hours-input' i]")
            .Filter(new() { Visible = true });

    // ---------------------------------------------------------------- "Add Timesheet Entry" dialog

    /// <summary>VERIFIED. Opens the "Create Time Entry" form.</summary>
    public static ILocator AddTimeEntryButton(IPage page) => page.Locator("#add-time-entry-button");

    /// <summary>VERIFIED. Task/project search button of the form ("Find a task, project, or internal time").</summary>
    public static ILocator TimeEntrySearchButton(IPage page) => DataCy(page, "time-registration-primary-modal-search-button");

    /// <summary>VERIFIED. Hours field of the form (placeholder "ex. 1h").</summary>
    public static ILocator TimeEntryHoursInput(IPage page) => DataCy(page, "new-time-entry-input");

    /// <summary>VERIFIED. Note field of the form.</summary>
    public static ILocator TimeEntryNotes(IPage page) => DataCy(page, "time-registration-modal-notes");

    /// <summary>VERIFIED. "Save Time Entry" button (never clicked by the discovery).</summary>
    public static ILocator TimeEntrySaveButton(IPage page) =>
        page.GetByRole(AriaRole.Button, new() { Name = "Save Time Entry", Exact = true });

    /// <summary>VERIFIED. Presence of the form (not an ARIA dialog: detected through its fields).</summary>
    public static ILocator TimeEntryForm(IPage page) => TimeEntrySearchButton(page).Or(TimeEntryHoursInput(page));

    /// <summary>UNVERIFIED. Options listed by the task search once opened.</summary>
    public static ILocator SearchOptions(IPage page) =>
        page.GetByRole(AriaRole.Option).Or(page.GetByRole(AriaRole.Listbox).GetByRole(AriaRole.Listitem));

    // ---------------------------------------------------------------- Approval (never clicked automatically)

    /// <summary>UNVERIFIED (absent from the capture). "Submit" button that sends the week for approval. Only probed.</summary>
    public static ILocator SubmitForApprovalButton(IPage page) =>
        page.GetByRole(AriaRole.Button, new() { NameRegex = Rx(@"^\s*(submit|soumettre)\b") });

    // ---------------------------------------------------------------- Task identifiers

    /// <summary>VERIFIED. Company-scoped identifiers: "T3382" for tasks, "P225" for projects.</summary>
    public static readonly Regex TaskCode = new(@"\bT\d{1,7}\b");

    public static readonly Regex ProjectCode = new(@"\bP-?\d{1,6}\b");

    /// <summary>All locators, by name, so the discovery can report how many elements each one matches.</summary>
    public static IReadOnlyList<(string Name, Func<IPage, ILocator> Locate)> Probes { get; } =
    [
        (nameof(LoginForm), LoginForm),
        (nameof(MyWorkNavigation), MyWorkNavigation),
        (nameof(TimesheetsPage), TimesheetsPage),
        (nameof(WeekViewToggle), WeekViewToggle),
        (nameof(TodayButton), TodayButton),
        (nameof(PreviousWeekButton), PreviousWeekButton),
        (nameof(NextWeekButton), NextWeekButton),
        (nameof(TimesheetTab), TimesheetTab),
        (nameof(FindTasksTab), FindTasksTab),
        (nameof(InternalTimeTab), InternalTimeTab),
        (nameof(AllDayCells), AllDayCells),
        (nameof(HourPopupInput), HourPopupInput),
        (nameof(AddTimeEntryButton), AddTimeEntryButton),
        (nameof(TimeEntrySearchButton), TimeEntrySearchButton),
        (nameof(TimeEntryHoursInput), TimeEntryHoursInput),
        (nameof(SearchOptions), SearchOptions),
        (nameof(SubmitForApprovalButton), SubmitForApprovalButton),
    ];
}

using System.Text.RegularExpressions;
using Microsoft.Playwright;

namespace TimeSheetHelper.Forecast;

/// <summary>
/// Every locator used against app.forecast.it, in one place.
///
/// ALL SELECTORS BELOW ARE UNVERIFIED GUESSES. Nobody on this project could log into Forecast while writing them;
/// they are based on the Forecast help center (labels such as "My Work", "My Timesheets", "Add Time Entry",
/// "Copy Previous Day/Week", "Show Suggestions", a weekly grid where you type into the cell of a task row)
/// and on common accessible patterns. Run "forecast discover" and fix them from the saved ARIA snapshots.
///
/// Rules: prefer role/text/aria based locators over CSS classes (Forecast's class names are generated);
/// every label has French and English variants because the UI language follows the user profile.
/// </summary>
public static class ForecastSelectors
{
    private const RegexOptions Ci = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;

    private static Regex Rx(string pattern) => new(pattern, Ci);

    // ---------------------------------------------------------------- Login / app shell

    /// <summary>UNVERIFIED. URL fragments of the Forecast login page and of the usual SSO providers.</summary>
    public static readonly Regex LoginUrl = Rx(
        @"/login|/signin|/sign-in|/sso|/auth|login\.microsoftonline\.com|accounts\.google\.com|okta\.com|onelogin\.com|auth0\.com");

    /// <summary>UNVERIFIED. Email/password fields of the Forecast login form.</summary>
    public static ILocator LoginForm(IPage page) =>
        page.Locator("input[type=email], input[type=password], input[name=email], input[autocomplete=username]");

    /// <summary>UNVERIFIED. Main navigation entry "My Work" (help center: "click My Work in the navigation bar").</summary>
    public static ILocator MyWorkNavigation(IPage page) =>
        page.GetByRole(AriaRole.Link, new() { NameRegex = Rx(@"^\s*(my work|mon travail)\s*$") })
            .Or(page.GetByRole(AriaRole.Button, new() { NameRegex = Rx(@"^\s*(my work|mon travail)\s*$") }))
            .Or(page.GetByText(Rx(@"^\s*(my work|mon travail)\s*$")));

    /// <summary>UNVERIFIED. Anything that only exists once logged in (navigation bar or the timesheet page itself).</summary>
    public static ILocator LoggedInMarker(IPage page) =>
        MyWorkNavigation(page)
            .Or(page.GetByRole(AriaRole.Navigation))
            .Or(AddTimeEntryButton(page));

    // ---------------------------------------------------------------- Week navigation

    /// <summary>UNVERIFIED. Toggle to make sure the timesheet shows a week (not a day).</summary>
    public static ILocator WeekViewToggle(IPage page) =>
        page.GetByRole(AriaRole.Tab, new() { NameRegex = Rx(@"^\s*(week|semaine)\s*$") })
            .Or(page.GetByRole(AriaRole.Radio, new() { NameRegex = Rx(@"^\s*(week|semaine)\s*$") }))
            .Or(page.GetByRole(AriaRole.Button, new() { NameRegex = Rx(@"^\s*(week|semaine)\s*$") }));

    /// <summary>UNVERIFIED. "Today"/"This week" button that brings the grid back to the current week.</summary>
    public static ILocator TodayButton(IPage page) =>
        page.GetByRole(AriaRole.Button, new() { NameRegex = Rx(@"^\s*(today|this week|aujourd.hui|cette semaine)\s*$") });

    /// <summary>UNVERIFIED. Arrow button to the previous week (often icon-only: relies on aria-label/title).</summary>
    public static ILocator PreviousWeekButton(IPage page) =>
        page.GetByRole(AriaRole.Button, new() { NameRegex = Rx(@"previous|prev|back|semaine pr[ée]c[ée]dente|pr[ée]c[ée]dent") })
            .Or(page.Locator("[aria-label*='previous' i], [title*='previous' i], [aria-label*='précédent' i], [title*='précédent' i]"));

    /// <summary>UNVERIFIED. Arrow button to the next week.</summary>
    public static ILocator NextWeekButton(IPage page) =>
        page.GetByRole(AriaRole.Button, new() { NameRegex = Rx(@"next|forward|semaine suivante|suivant") })
            .Or(page.Locator("[aria-label*='next' i], [title*='next' i], [aria-label*='suivant' i], [title*='suivant' i]"));

    // ---------------------------------------------------------------- Adding a task / time entry

    /// <summary>UNVERIFIED. Help center: "Add Time Entry" button at the top right of the calendar (opens a form).</summary>
    public static ILocator AddTimeEntryButton(IPage page) =>
        page.GetByRole(AriaRole.Button, new() { NameRegex = Rx(@"add time( entry)?|ajouter (une )?(entr[ée]e de )?temps|nouvelle entr[ée]e") });

    /// <summary>UNVERIFIED. Button/link below the grid that adds a task row to the week ("Add task", "Add row").</summary>
    public static ILocator AddRowButton(IPage page) =>
        page.GetByRole(AriaRole.Button, new() { NameRegex = Rx(@"add (a )?(task|row|line)|ajouter (une )?(t[âa]che|ligne)") })
            .Or(page.GetByText(Rx(@"^\s*(\+\s*)?(add (a )?(task|row|line)|ajouter (une )?(t[âa]che|ligne))\s*$")));

    /// <summary>UNVERIFIED. Search field to find a task/project/internal time ("search for a task, project...").</summary>
    public static ILocator TaskSearchInput(IPage page) =>
        page.GetByRole(AriaRole.Combobox)
            .Or(page.GetByPlaceholder(Rx(@"search|rechercher|find|trouver|t[âa]che|task|project|projet")))
            .Or(page.GetByRole(AriaRole.Searchbox));

    /// <summary>UNVERIFIED. Options of the task search dropdown.</summary>
    public static ILocator SearchOptions(IPage page) =>
        page.GetByRole(AriaRole.Option)
            .Or(page.GetByRole(AriaRole.Listbox).GetByRole(AriaRole.Listitem));

    /// <summary>UNVERIFIED. Any modal dialog (time entry form, task modal).</summary>
    public static ILocator Dialog(IPage page) =>
        page.GetByRole(AriaRole.Dialog).Or(page.GetByRole(AriaRole.Alertdialog));

    /// <summary>UNVERIFIED. Button that closes a dialog without saving.</summary>
    public static ILocator DialogCancelButton(ILocator dialog) =>
        dialog.GetByRole(AriaRole.Button, new() { NameRegex = Rx(@"^\s*(cancel|close|annuler|fermer|×|x)\s*$") });

    // ---------------------------------------------------------------- Week grid

    /// <summary>UNVERIFIED. The weekly timesheet grid (help center: "the grid below the calendar").</summary>
    public static ILocator WeekGrid(IPage page) =>
        page.GetByRole(AriaRole.Grid)
            .Or(page.GetByRole(AriaRole.Table))
            .Or(page.Locator("[class*='timesheet' i][class*='table' i], [class*='timesheets' i] table, [data-cy*='timesheet' i]"))
            .First;

    /// <summary>UNVERIFIED. Column headers of the grid; expected to contain the day name and day of month ("Mon 6", "lun. 6").</summary>
    public static ILocator ColumnHeaders(ILocator grid) =>
        grid.GetByRole(AriaRole.Columnheader);

    /// <summary>UNVERIFIED. Data rows of the grid (one per task/project/internal time).</summary>
    public static ILocator Rows(ILocator grid) =>
        grid.GetByRole(AriaRole.Row);

    /// <summary>UNVERIFIED. Editable hour inputs inside a row, expected in day order.</summary>
    public static ILocator HourInputs(ILocator row) =>
        row.Locator("input:not([type=hidden]):not([type=checkbox]):not([type=radio]), [contenteditable=true]");

    // ---------------------------------------------------------------- Approval (never clicked automatically)

    /// <summary>UNVERIFIED. "Submit" button that sends the week for manager approval. Only probed by discovery.</summary>
    public static ILocator SubmitForApprovalButton(IPage page) =>
        page.GetByRole(AriaRole.Button, new() { NameRegex = Rx(@"submit|soumettre|envoyer pour approbation") });

    // ---------------------------------------------------------------- Task identifiers

    /// <summary>
    /// UNVERIFIED. Forecast shows company-scoped identifiers such as "T1234" for tasks and "P-12"/"P12" for projects.
    /// </summary>
    public static readonly Regex TaskCode = new(@"\bT\d{1,7}\b", RegexOptions.CultureInvariant);

    public static readonly Regex ProjectCode = new(@"\bP-?\d{1,6}\b", RegexOptions.CultureInvariant);

    /// <summary>All locators, by name, so the discovery can report how many elements each one matches.</summary>
    public static IReadOnlyList<(string Name, Func<IPage, ILocator> Locate)> Probes { get; } =
    [
        (nameof(LoginForm), LoginForm),
        (nameof(MyWorkNavigation), MyWorkNavigation),
        (nameof(LoggedInMarker), LoggedInMarker),
        (nameof(WeekViewToggle), WeekViewToggle),
        (nameof(TodayButton), TodayButton),
        (nameof(PreviousWeekButton), PreviousWeekButton),
        (nameof(NextWeekButton), NextWeekButton),
        (nameof(AddTimeEntryButton), AddTimeEntryButton),
        (nameof(AddRowButton), AddRowButton),
        (nameof(TaskSearchInput), TaskSearchInput),
        (nameof(SearchOptions), SearchOptions),
        (nameof(Dialog), Dialog),
        (nameof(WeekGrid), WeekGrid),
        ("ColumnHeaders", p => ColumnHeaders(WeekGrid(p))),
        ("Rows", p => Rows(WeekGrid(p))),
        ("HourInputs", p => HourInputs(WeekGrid(p))),
        (nameof(SubmitForApprovalButton), SubmitForApprovalButton),
    ];
}

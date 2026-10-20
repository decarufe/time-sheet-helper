using System.Text.RegularExpressions;
using TimeSheetHelper.App.Components.Layout;

namespace TimeSheetHelper.Tests.App;

/// <summary>
/// The shell components cannot be rendered in these tests (no component test host, no browser), so these tests pin the
/// contracts that tie its files together: routes, rail sections, icons, panel descriptions, landmarks, JS interop and theme tokens.
/// </summary>
public sealed class ShellMarkupTests
{
    private static readonly string AppRoot = FindAppRoot();
    private static string Layout => Path.Combine(AppRoot, "Components", "Layout");

    private static string FindAppRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "TimeSheetHelper.sln")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        return Path.Combine(dir.FullName, "src", "TimeSheetHelper.App");
    }

    private static string Read(params string[] segments) => File.ReadAllText(Path.Combine([AppRoot, .. segments]));

    private static string LayoutFile(string name) => File.ReadAllText(Path.Combine(Layout, name));

    private static IEnumerable<string> Matches(string text, string pattern) =>
        Regex.Matches(text, pattern).Select(m => m.Groups[1].Value);

    // Rendered shell parts; ReconnectModal is the framework template and PageHeader renders nothing.
    private static IEnumerable<string> ShellRazorFiles() =>
        Directory.GetFiles(Layout, "*.razor").Where(f => !Path.GetFileName(f).StartsWith("ReconnectModal", StringComparison.Ordinal));

    [Fact]
    public void Every_destination_route_is_served_by_a_page()
    {
        var pages = string.Join('\n', Directory.GetFiles(Path.Combine(AppRoot, "Components", "Pages"), "*.razor").Select(File.ReadAllText));

        foreach (var destination in ShortcutMap.Destinations)
            Assert.Matches($"(?m)^@page \"/{Regex.Escape(destination.Route)}\"", pages);
    }

    [Fact]
    public void Every_destination_has_a_description_in_the_context_panel()
    {
        var panel = LayoutFile("ContextPanel.razor");
        var described = Matches(panel, "\\[\"([a-z]*)\"\\]\\s*=").ToHashSet();

        Assert.Equal(ShortcutMap.Destinations.Select(d => d.Route).Order(), described.Order());
    }

    [Fact]
    public void Every_destination_and_rail_icon_exists_in_the_icon_set()
    {
        var icons = Matches(LayoutFile("Icon.razor"), "\\[\"(\\w+)\"\\]\\s*=").ToHashSet();
        var used = ShortcutMap.Destinations.Select(d => d.Icon).Concat(Rail.Sections.Select(s => s.Icon)).Distinct();

        Assert.All(used, icon => Assert.Contains(icon, icons));
    }

    [Fact]
    public void Rail_has_one_link_per_section_and_each_section_has_a_destination()
    {
        var sections = Rail.Sections;

        Assert.Equal(Enum.GetValues<NavSection>(), sections.Select(s => s.Section));
        Assert.All(sections, s => Assert.Contains(ShortcutMap.Destinations, d => d.Section == s.Section));
        Assert.Equal(sections.Count, sections.Select(s => s.Name).Distinct().Count());
        Assert.All(sections, s => Assert.False(string.IsNullOrWhiteSpace(s.Tip)));
    }

    [Fact]
    public void Every_nav_landmark_is_labelled_and_the_labels_are_distinct()
    {
        var navs = ShellRazorFiles()
            .SelectMany(f => Regex.Matches(File.ReadAllText(f), "<nav\\b[^>]*>").Select(m => m.Value))
            .ToList();

        Assert.NotEmpty(navs);
        var labels = navs.Select(n => Regex.Match(n, "aria-label=\"([^\"]+)\"")).ToList();
        Assert.All(labels, l => Assert.True(l.Success, "A <nav> without aria-label"));
        Assert.Equal(labels.Count, labels.Select(l => l.Groups[1].Value).Distinct().Count());
    }

    [Fact]
    public void Current_link_of_each_navigation_is_exposed_with_aria_current()
    {
        Assert.Contains("aria-current=\"@(active ? \"true\" : null)\"", LayoutFile("Rail.razor"));
        Assert.Contains("aria-current=\"@(active ? \"true\" : null)\"", LayoutFile("MobileNav.razor"));
        Assert.Contains("aria-current=\"@(current ? \"page\" : null)\"", LayoutFile("MobileNav.razor"));
        Assert.Contains("aria-current=\"@(current ? \"page\" : null)\"", LayoutFile("ContextPanel.razor"));
        Assert.Contains("aria-current=\"@(current ? \"step\" : null)\"", LayoutFile("StepIndicator.razor"));
    }

    [Fact]
    public void Stepper_steps_lead_to_existing_destinations_in_order()
    {
        var stepper = LayoutFile("StepIndicator.razor");
        var steps = Regex.Matches(stepper, "\\(NavStep\\.(\\w+),\\s*\"([^\"]+)\",\\s*\"([^\"]*)\"\\)").ToList();

        Assert.Equal(["Enter", "Review", "Submit"], steps.Select(s => s.Groups[1].Value));
        Assert.Equal(["Saisir", "Vérifier", "Soumettre"], steps.Select(s => s.Groups[2].Value));
        Assert.All(steps.Where(s => s.Groups[3].Value.Length > 0), s =>
            Assert.Contains(ShortcutMap.Destinations, d => d.Route == s.Groups[3].Value));
    }

    [Fact]
    public void The_page_has_one_h1_a_skip_link_and_a_focusable_main_target()
    {
        var h1s = ShellRazorFiles()
            .SelectMany(f => Regex.Matches(Regex.Replace(File.ReadAllText(f), "@\\*[\\s\\S]*?\\*@", ""), "<h1\\b").Select(_ => f))
            .ToList();
        Assert.Single(h1s);
        Assert.Equal("WeekHeader.razor", Path.GetFileName(h1s[0]));

        var layout = LayoutFile("MainLayout.razor");
        Assert.Contains("class=\"skip-link\"", layout);
        Assert.Contains("#main", layout);
        Assert.Matches("<main\\b[^>]*id=\"main\"[^>]*tabindex=\"-1\"", layout);
    }

    [Fact]
    public void Week_header_names_its_controls_and_progress()
    {
        var header = LayoutFile("WeekHeader.razor");

        Assert.Contains("aria-label=\"Semaine précédente\"", header);
        Assert.Contains("aria-label=\"Semaine suivante\"", header);
        Assert.Matches("role=\"progressbar\"", header);
        Assert.Contains("aria-label=\"Progression de la semaine\"", header);
    }

    [Fact]
    public void Layout_only_calls_functions_exported_by_shell_js()
    {
        var js = Read("wwwroot", "shell.js");
        var exported = Matches(js, "(?m)^export function (\\w+)").ToHashSet();
        var layout = LayoutFile("MainLayout.razor");

        Assert.Contains("\"./shell.js\"", layout);
        var called = Matches(layout, "module\\.Invoke(?:Void)?Async(?:<\\w+>)?\\(\"(\\w+)\"")
            .Concat(Matches(layout, "CallModuleAsync\\(\"(\\w+)\""))
            .ToHashSet();

        Assert.Equal(["dispose", "getPrefs", "init", "setDrawerOpen", "setPanel", "setTheme"], called.Order());
        Assert.All(called, name => Assert.Contains(name, exported));
        Assert.Contains("OnShortcut", layout);
        Assert.Contains("invokeMethodAsync(\"OnShortcut\"", js);
    }

    [Fact]
    public void Theme_and_panel_storage_keys_match_between_shell_js_and_the_anti_flash_script()
    {
        var js = Read("wwwroot", "shell.js");
        var app = Read("Components", "App.razor");

        foreach (var key in new[] { "tsh.theme", "tsh.panel" })
        {
            Assert.Contains($"\"{key}\"", js);
            Assert.Contains($"localStorage.getItem(\"{key}\")", app);
        }
    }

    [Fact]
    public void Shell_styles_use_theme_tokens_instead_of_hard_coded_colours()
    {
        foreach (var file in Directory.GetFiles(Layout, "*.razor.css").Where(f => !Path.GetFileName(f).StartsWith("ReconnectModal", StringComparison.Ordinal)))
        {
            // The framework error banner of the template is not themed on purpose.
            var css = Regex.Replace(File.ReadAllText(file), "#blazor-error-ui[^{]*\\{[^}]*\\}", "");

            Assert.False(Regex.IsMatch(css, "#[0-9a-fA-F]{3,8}\\b|\\b(?:rgb|hsl)a?\\("), $"{Path.GetFileName(file)} hard-codes a colour");
        }
    }

    [Fact]
    public void Only_the_main_target_removes_the_focus_outline()
    {
        foreach (var file in Directory.GetFiles(Layout, "*.razor.css"))
        {
            var css = File.ReadAllText(file);
            foreach (Match rule in Regex.Matches(css, "([^{}]+)\\{([^}]*)\\}"))
            {
                if (Regex.IsMatch(rule.Groups[2].Value, "outline\\s*:\\s*(none|0)\\b"))
                    Assert.Equal("main.page:focus", rule.Groups[1].Value.Trim());
            }
        }
    }

    [Fact]
    public void Every_theme_token_used_by_the_shell_is_declared()
    {
        var appCss = Read("wwwroot", "app.css");
        var shellCss = string.Join('\n', Directory.GetFiles(Layout, "*.razor.css")
            .Where(f => !Path.GetFileName(f).StartsWith("ReconnectModal", StringComparison.Ordinal))
            .Select(File.ReadAllText));
        var declared = Matches(appCss + "\n" + shellCss, "(--[\\w-]+)\\s*:").ToHashSet();
        var used = Matches(shellCss, "var\\((--[\\w-]+)\\s*\\)").Distinct().ToList();

        Assert.NotEmpty(used);
        Assert.All(used, token => Assert.Contains(token, declared));
    }

    [Fact]
    public void App_css_defines_both_themes_and_a_reduced_motion_mode_that_keeps_the_spinner()
    {
        var css = Read("wwwroot", "app.css");

        Assert.Contains(":root[data-theme=\"light\"]", css);
        Assert.Contains(":root[data-theme=\"dark\"]", css);
        Assert.Matches("@media \\(prefers-reduced-motion: reduce\\)[\\s\\S]*\\.spin", css);
    }
}

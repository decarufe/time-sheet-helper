using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TimeSheetHelper.Collectors.Windows;

namespace TimeSheetHelper.Tests;

public class FileContextResolverTests
{
    private sealed class StaticOptions(FocusTrackerOptions value) : IOptionsMonitor<FocusTrackerOptions>
    {
        public FocusTrackerOptions CurrentValue => value;
        public FocusTrackerOptions Get(string? name) => value;
        public IDisposable? OnChange(Action<FocusTrackerOptions, string?> listener) => null;
    }

    private readonly FileContextResolver _sut = new(new StaticOptions(new()), NullLogger<FileContextResolver>.Instance);

    [Theory]
    [InlineData("Program.cs - time-sheet-helper - Visual Studio Code", "Program.cs", "time-sheet-helper")]
    [InlineData("● Home.razor - time-sheet-helper - Visual Studio Code", "Home.razor", "time-sheet-helper")]
    public void Vs_code_title_gives_file_and_workspace(string title, string file, string folder)
    {
        var context = _sut.FromTitle("Code", title);
        Assert.Equal(file, context.FilePath);
        Assert.Equal(folder, context.Folder);
    }

    [Fact]
    public void Unknown_process_gives_nothing() =>
        Assert.Equal(new FileContext(null, null), _sut.FromTitle("foo", "bar"));

    [Fact]
    public void SharePoint_url_keeps_url_and_parent()
    {
        var context = FileContextResolver.FromPath("https://contoso.sharepoint.com/sites/x/Shared Documents/Plan.docx");
        Assert.Equal("https://contoso.sharepoint.com/sites/x/Shared Documents", context.Folder);
        Assert.NotNull(context.Url);
    }

    [Fact]
    public void Local_path_gives_directory() =>
        Assert.Equal(@"C:\docs", FileContextResolver.FromPath(@"C:\docs\Plan.docx").Folder);
}

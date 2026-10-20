using TimeSheetHelper.App;

namespace TimeSheetHelper.Tests.App;

public class TrayIconTests
{
    [Fact]
    public void LoadAppIcon_returns_the_embedded_icon()
    {
        using var icon = TrayIcon.LoadAppIcon();

        Assert.NotNull(icon);
        Assert.True(icon.Width >= 16);
    }
}

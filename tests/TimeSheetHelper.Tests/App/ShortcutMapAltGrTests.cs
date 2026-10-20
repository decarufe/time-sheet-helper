using TimeSheetHelper.App.Components.Layout;

namespace TimeSheetHelper.Tests.App;

public class ShortcutMapAltGrTests
{
    // AZERTY/CSA: "[" and "]" need AltGr, which the browser reports as Ctrl+Alt.
    [Theory]
    [InlineData("[", ShortcutKind.PreviousWeek)]
    [InlineData("]", ShortcutKind.NextWeek)]
    public void AltGr_bracket_changes_week(string key, ShortcutKind expected)
    {
        var action = ShortcutMap.Resolve(new KeyChord(key, Alt: true, Ctrl: true, Meta: false, Shift: false, InEditableField: false));

        Assert.Equal(expected, action.Kind);
    }

    [Fact]
    public void AltGr_bracket_in_a_field_is_left_to_the_field()
    {
        var action = ShortcutMap.Resolve(new KeyChord("[", Alt: true, Ctrl: true, Meta: false, Shift: false, InEditableField: true));

        Assert.Equal(ShortcutKind.None, action.Kind);
    }

    [Theory]
    [InlineData("1")]
    [InlineData("t")]
    public void AltGr_digit_or_t_is_not_an_alt_shortcut(string key)
    {
        var action = ShortcutMap.Resolve(new KeyChord(key, Alt: true, Ctrl: true, Meta: false, Shift: false, InEditableField: false));

        Assert.Equal(ShortcutKind.None, action.Kind);
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public void Ctrl_or_Meta_bracket_is_not_a_week_change(bool ctrl, bool meta)
    {
        var action = ShortcutMap.Resolve(new KeyChord("[", Alt: false, Ctrl: ctrl, Meta: meta, Shift: false, InEditableField: false));

        Assert.Equal(ShortcutKind.None, action.Kind);
    }
}

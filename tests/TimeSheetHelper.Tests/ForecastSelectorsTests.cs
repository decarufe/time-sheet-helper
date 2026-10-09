using System.Reflection;
using System.Text.RegularExpressions;
using TimeSheetHelper.Forecast;

namespace TimeSheetHelper.Tests;

public class ForecastSelectorsTests
{
    public static TheoryData<string> SelectorRegexes()
    {
        var data = new TheoryData<string>();
        foreach (var field in typeof(ForecastSelectors).GetFields(BindingFlags.Public | BindingFlags.Static).Where(f => f.FieldType == typeof(Regex)))
            data.Add(field.Name);
        return data;
    }

    [Theory]
    [MemberData(nameof(SelectorRegexes))]
    public void Selector_regexes_only_use_options_playwright_can_translate(string fieldName)
    {
        var regex = (Regex)typeof(ForecastSelectors).GetField(fieldName)!.GetValue(null)!;
        Assert.Equal(RegexOptions.None, regex.Options & ~ForecastSelectors.PlaywrightSupportedOptions);
    }
}

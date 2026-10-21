using System.Diagnostics.CodeAnalysis;
using System.Reflection;

namespace TimeSheetHelper.App.Updates;

/// <summary>A semantic version (major.minor.patch[-prerelease][+build]); build metadata is ignored when comparing.</summary>
public sealed record AppVersion(int Major, int Minor, int Patch, string Prerelease = "") : IComparable<AppVersion>
{
    public bool IsPrerelease => Prerelease.Length > 0;

    public static bool TryParse(string? text, [NotNullWhen(true)] out AppVersion? version)
    {
        version = null;
        var s = text?.Trim();
        if (string.IsNullOrEmpty(s))
            return false;
        if (s[0] is 'v' or 'V')
            s = s[1..];

        var plus = s.IndexOf('+');
        if (plus >= 0)
            s = s[..plus];

        var prerelease = "";
        var dash = s.IndexOf('-');
        if (dash >= 0)
        {
            prerelease = s[(dash + 1)..];
            s = s[..dash];
            if (prerelease.Length == 0)
                return false;
        }

        var parts = s.Split('.');
        if (parts.Length is < 2 or > 3
            || !int.TryParse(parts[0], out var major)
            || !int.TryParse(parts[1], out var minor)
            || major < 0 || minor < 0)
            return false;

        var patch = 0;
        if (parts.Length == 3 && (!int.TryParse(parts[2], out patch) || patch < 0))
            return false;

        version = new AppVersion(major, minor, patch, prerelease);
        return true;
    }

    /// <summary>The version of the running executable (InformationalVersion, set by the release build).</summary>
    public static AppVersion Current()
    {
        var assembly = typeof(AppVersion).Assembly;
        var informational = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (TryParse(informational, out var version))
            return version;
        var v = assembly.GetName().Version;
        return v is null ? new AppVersion(0, 0, 0) : new AppVersion(v.Major, v.Minor, Math.Max(v.Build, 0));
    }

    public int CompareTo(AppVersion? other)
    {
        if (other is null)
            return 1;
        var c = Major.CompareTo(other.Major);
        if (c != 0) return c;
        c = Minor.CompareTo(other.Minor);
        if (c != 0) return c;
        c = Patch.CompareTo(other.Patch);
        if (c != 0) return c;
        return ComparePrerelease(Prerelease, other.Prerelease);
    }

    // A version without a prerelease tag is greater than the same version with one; identifiers compare one by one.
    private static int ComparePrerelease(string a, string b)
    {
        if (a.Length == 0 || b.Length == 0)
            return a.Length == b.Length ? 0 : a.Length == 0 ? 1 : -1;

        var left = a.Split('.');
        var right = b.Split('.');
        for (var i = 0; i < Math.Min(left.Length, right.Length); i++)
        {
            var leftNumeric = int.TryParse(left[i], out var ln);
            var rightNumeric = int.TryParse(right[i], out var rn);
            var c = (leftNumeric, rightNumeric) switch
            {
                (true, true) => ln.CompareTo(rn),
                (true, false) => -1,
                (false, true) => 1,
                _ => string.CompareOrdinal(left[i], right[i]),
            };
            if (c != 0)
                return Math.Sign(c);
        }
        return left.Length.CompareTo(right.Length);
    }

    public bool Equals(AppVersion? other) => other is not null && CompareTo(other) == 0;

    public override int GetHashCode() => HashCode.Combine(Major, Minor, Patch, Prerelease);

    public override string ToString() => IsPrerelease ? $"{Major}.{Minor}.{Patch}-{Prerelease}" : $"{Major}.{Minor}.{Patch}";

    public static bool operator <(AppVersion? a, AppVersion? b) => a is null ? b is not null : a.CompareTo(b) < 0;
    public static bool operator >(AppVersion? a, AppVersion? b) => a is not null && a.CompareTo(b) > 0;
    public static bool operator <=(AppVersion? a, AppVersion? b) => !(a > b);
    public static bool operator >=(AppVersion? a, AppVersion? b) => !(a < b);
}

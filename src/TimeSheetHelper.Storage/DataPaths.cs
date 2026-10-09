namespace TimeSheetHelper.Storage;

public static class DataPaths
{
    /// <summary>Environment variable that relocates all local data (tests, throwaway runs).</summary>
    public const string OverrideVariable = "TIMESHEETHELPER_DATA_DIR";

    public static string Root
    {
        get
        {
            var root = Environment.GetEnvironmentVariable(OverrideVariable) is { Length: > 0 } custom
                ? custom
                : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TimeSheetHelper");
            Directory.CreateDirectory(root);
            return root;
        }
    }

    public static string DatabaseConnectionString => $"Data Source={Path.Combine(Root, "data.db")}";
}

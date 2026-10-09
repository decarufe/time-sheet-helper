namespace TimeSheetHelper.Storage;

public static class DataPaths
{
    public static string Root
    {
        get
        {
            var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TimeSheetHelper");
            Directory.CreateDirectory(root);
            return root;
        }
    }

    public static string DatabaseConnectionString => $"Data Source={Path.Combine(Root, "data.db")}";
}

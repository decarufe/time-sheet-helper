namespace TimeSheetHelper.App.About;

/// <summary>What the "À propos" page and the tray dialog show about the running application.</summary>
public sealed record AboutInfo(
    string Version,
    string RuntimeVersion,
    string RepositoryUrl,
    string License,
    string DataFolder,
    string DatabasePath,
    long DatabaseBytes,
    long ActivityCount,
    long ActivityBytes);

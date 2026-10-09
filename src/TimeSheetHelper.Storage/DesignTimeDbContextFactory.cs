using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace TimeSheetHelper.Storage;

internal sealed class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<TimeSheetDbContext>
{
    public TimeSheetDbContext CreateDbContext(string[] args) =>
        new(new DbContextOptionsBuilder<TimeSheetDbContext>().UseSqlite("Data Source=design.db").Options);
}

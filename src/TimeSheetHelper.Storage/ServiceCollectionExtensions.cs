using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using TimeSheetHelper.Core.Activities;

namespace TimeSheetHelper.Storage;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddTimeSheetStorage(this IServiceCollection services, string connectionString)
    {
        services.AddDbContextFactory<TimeSheetDbContext>(o => o.UseSqlite(connectionString));
        services.AddSingleton<ActivityStore>();
        services.AddSingleton<IActivitySink>(sp => sp.GetRequiredService<ActivityStore>());
        return services;
    }
}

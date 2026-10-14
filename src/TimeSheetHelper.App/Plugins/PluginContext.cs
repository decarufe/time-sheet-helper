using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using TimeSheetHelper.Core.Activities;
using TimeSheetHelper.Core.Secrets;
using TimeSheetHelper.Plugins;

namespace TimeSheetHelper.App.Plugins;

internal sealed class PluginContext(
    IConfiguration configuration,
    IActivitySink sink,
    IActivityHistory history,
    ISecretStore secrets,
    ILoggerFactory loggerFactory,
    TimeProvider time,
    string dataDirectory) : IPluginContext
{
    public IConfiguration Configuration => configuration;
    public IActivitySink Sink => sink;
    public IActivityHistory History => history;
    public ISecretStore Secrets => secrets;
    public ILoggerFactory LoggerFactory => loggerFactory;
    public TimeProvider Time => time;

    public string DataDirectory
    {
        get
        {
            Directory.CreateDirectory(dataDirectory);
            return dataDirectory;
        }
    }
}

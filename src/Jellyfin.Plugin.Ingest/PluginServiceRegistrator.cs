using System.IO;
using Jellyfin.Plugin.Ingest.Service;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Plugins;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Ingest;

/// <summary>
/// Registers the plugin's services with Jellyfin's dependency injection container.
/// </summary>
public class PluginServiceRegistrator : IPluginServiceRegistrator
{
    /// <inheritdoc />
    public void RegisterServices(IServiceCollection serviceCollection, IServerApplicationHost applicationHost)
    {
        // Same folder as the plugin's DataFolderPath, resolved without needing the plugin instance to exist yet
        serviceCollection.AddSingleton(sp => new IngestStateStore(
            Path.Combine(sp.GetRequiredService<IApplicationPaths>().PluginsPath, typeof(IngestPlugin).Assembly.GetName().Name!, "state.json"),
            sp.GetRequiredService<ILogger<IngestStateStore>>()));
        serviceCollection.AddSingleton<IngestProgress>();
        serviceCollection.AddHostedService<IngestService>();
    }
}

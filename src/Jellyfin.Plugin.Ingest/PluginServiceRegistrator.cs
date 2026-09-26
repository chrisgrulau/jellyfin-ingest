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
        serviceCollection.AddSingleton(sp => IngestPaths.UnderPlugins(sp.GetRequiredService<IApplicationPaths>().PluginsPath));
        serviceCollection.AddSingleton(sp => new IngestStateStore(sp.GetRequiredService<IngestPaths>().StateFile, sp.GetRequiredService<ILogger<IngestStateStore>>()));
        serviceCollection.AddSingleton<IngestProgress>();
        serviceCollection.AddHostedService<IngestService>();
    }
}

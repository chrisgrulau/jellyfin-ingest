using System;
using System.Collections.Generic;
using System.Globalization;
using Jellyfin.Plugin.Ingest.Configuration;
using Jellyfin.Plugin.Ingest.Planning;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;

namespace Jellyfin.Plugin.Ingest;

/// <summary>
/// Jellyfin Ingest: watches drop folders and files new media into the libraries under Jellyfin's naming standard.
/// </summary>
public class IngestPlugin : BasePlugin<PluginConfiguration>, IHasWebPages
{
    /// <summary>
    /// Initializes a new instance of the <see cref="IngestPlugin"/> class.
    /// </summary>
    /// <param name="applicationPaths">Instance of the <see cref="IApplicationPaths"/> interface.</param>
    /// <param name="xmlSerializer">Instance of the <see cref="IXmlSerializer"/> interface.</param>
    public IngestPlugin(IApplicationPaths applicationPaths, IXmlSerializer xmlSerializer)
        : base(applicationPaths, xmlSerializer)
    {
        Instance = this;

        // Watch folders saved before dry run was per folder keep the old global setting (ING-32)
        if (WatchFolder.MigrateDryRun(Configuration.WatchFolders, Configuration.DryRun))
        {
            SaveConfiguration();
        }
    }

    /// <inheritdoc />
    public override string Name => "Shoal Ingest";

    /// <inheritdoc />
    public override Guid Id => Guid.Parse("88d6ced9-052e-45f6-b21d-fc4eefa7998d");

    /// <inheritdoc />
    public override string Description => "Watches drop folders and files new media into your libraries, correctly named.";

    /// <inheritdoc />
    public override void UpdateConfiguration(BasePluginConfiguration configuration)
    {
        // Keep values the service relies on within safe bounds, whatever the settings page (or an API client) sent
        if (configuration is PluginConfiguration c)
        {
            c.QuarantineRetentionDays = Math.Max(1, c.QuarantineRetentionDays);
            c.SettleSeconds = Math.Max(5, c.SettleSeconds);
            c.QuarantinePath = PathGuard.Tidy(c.QuarantinePath);

            // One spelling per folder ("/in/" is "/in"), so everything keyed by a watch folder agrees
            foreach (var w in c.WatchFolders)
            {
                w.Path = PathGuard.Tidy(w.Path);
            }

            // A folder sent without its own dry-run setting (an older page or an API client) takes the fallback
            WatchFolder.MigrateDryRun(c.WatchFolders, c.DryRun);
        }

        base.UpdateConfiguration(configuration);
    }

    /// <summary>
    /// Gets the current plugin instance.
    /// </summary>
    public static IngestPlugin? Instance { get; private set; }

    /// <inheritdoc />
    public IEnumerable<PluginPageInfo> GetPages()
    {
        return
        [
            new PluginPageInfo
            {
                Name = Name,
                EmbeddedResourcePath = string.Format(CultureInfo.InvariantCulture, "{0}.Configuration.configPage.html", GetType().Namespace),

                // In the dashboard's menu, so the review queue is one click away (FAM-05)
                EnableInMainMenu = true,
                MenuSection = "server",
                MenuIcon = "move_to_inbox",
                DisplayName = "Shoal Ingest",
            }
        ];
    }
}

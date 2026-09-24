using System;
using System.Collections.Generic;
using System.Globalization;
using Jellyfin.Plugin.Ingest.Configuration;
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
    }

    /// <inheritdoc />
    public override string Name => "Ingest";

    /// <inheritdoc />
    public override Guid Id => Guid.Parse("88d6ced9-052e-45f6-b21d-fc4eefa7998d");

    /// <inheritdoc />
    public override string Description => "Watches drop folders and files new media into your libraries, correctly named.";

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
                EmbeddedResourcePath = string.Format(CultureInfo.InvariantCulture, "{0}.Configuration.configPage.html", GetType().Namespace)
            }
        ];
    }
}

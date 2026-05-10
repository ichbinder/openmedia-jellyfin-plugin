using System;
using System.Collections.Generic;
using System.Globalization;
using Jellyfin.Plugin.OpenMedia.Configuration;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;

namespace Jellyfin.Plugin.OpenMedia;

/// <summary>
/// Einstiegspunkt fuer das openmedia Jellyfin-Plugin.
/// </summary>
public class Plugin : BasePlugin<PluginConfiguration>, IHasWebPages
{
    /// <summary>Eindeutige Plugin-GUID — identisch mit dem Wert in configPage.html und manifest.json.</summary>
    public static readonly Guid PluginId = new Guid("8cfc3c6a-c39f-467f-8ebe-9f3218724aa1");

    public Plugin(IApplicationPaths applicationPaths, IXmlSerializer xmlSerializer)
        : base(applicationPaths, xmlSerializer)
    {
        Instance = this;
    }

    public static Plugin? Instance { get; private set; }

    public override string Name => "openmedia";

    public override string Description =>
        "Streamt deine openmedia-UserLibrary direkt nach Jellyfin (Direct-Play von S3).";

    public override Guid Id => PluginId;

    public IEnumerable<PluginPageInfo> GetPages()
    {
        return new[]
        {
            new PluginPageInfo
            {
                Name = Name,
                EmbeddedResourcePath = string.Format(
                    CultureInfo.InvariantCulture,
                    "{0}.Configuration.configPage.html",
                    GetType().Namespace)
            }
        };
    }
}

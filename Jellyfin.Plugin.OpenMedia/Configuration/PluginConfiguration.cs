using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.OpenMedia.Configuration;

/// <summary>
/// Persistierte Plugin-Settings. XML-serialisiert nach
/// ~/Library/Application Support/jellyfin/plugins/configurations/Jellyfin.Plugin.OpenMedia.xml
/// </summary>
public class PluginConfiguration : BasePluginConfiguration
{
    /// <summary>Basis-URL der openmedia-API, z.B. https://api.mediatoken.de</summary>
    public string ApiUrl { get; set; } = "https://api.mediatoken.de";

    /// <summary>om_-API-Token des Users (aus dem openmedia-Profil kopiert)</summary>
    public string ApiToken { get; set; } = string.Empty;
}

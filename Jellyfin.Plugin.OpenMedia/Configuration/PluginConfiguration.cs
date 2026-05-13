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

    /// <summary>
    /// Absoluter Pfad zum Verzeichnis in das die STRM-Dateien synchronisiert werden.
    /// Leer = Default unter ApplicationPaths.DataPath/openmedia-strm.
    /// </summary>
    public string StrmDirectory { get; set; } = string.Empty;

    /// <summary>
    /// Name der virtuellen Jellyfin-Library die das Plugin beim Start automatisch anlegt
    /// (falls AutoCreateLibrary aktiv ist und keine Library mit diesem Namen existiert).
    /// </summary>
    public string LibraryName { get; set; } = "openmedia";

    /// <summary>
    /// Wenn true legt das Plugin beim Start automatisch eine Movies-Library mit Namen
    /// <see cref="LibraryName"/> an die auf <see cref="StrmDirectory"/> zeigt.
    /// </summary>
    public bool AutoCreateLibrary { get; set; } = true;
}

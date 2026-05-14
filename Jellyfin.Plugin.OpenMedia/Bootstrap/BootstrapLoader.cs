using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.OpenMedia.Bootstrap;

/// <summary>
/// Liest eine optionale <c>bootstrap.json</c> neben dem Plugin-Assembly und
/// uebergibt <c>{apiUrl, apiToken}</c> an die uebergebene Apply-Callback.
/// Datei wird nach erfolgreichem Apply geloescht; bei Parse-Fehler nach
/// <c>bootstrap.json.invalid</c> umbenannt. Niemals throwing — Bootstrap ist
/// eine Komfortfunktion, das Plugin muss auch ohne sie starten.
/// </summary>
public static class BootstrapLoader
{
    /// <summary>Dateiname relativ zum Plugin-Verzeichnis.</summary>
    public const string FileName = "bootstrap.json";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };

    /// <summary>
    /// Sucht nach <c>bootstrap.json</c> in <paramref name="pluginDirectory"/> und
    /// uebergibt die enthaltenen Werte an <paramref name="apply"/>.
    /// </summary>
    /// <param name="pluginDirectory">Plugin-Verzeichnis (typisch: Verzeichnis des Plugin-DLL).</param>
    /// <param name="apply">Wird mit <c>(apiUrl, apiToken)</c> aufgerufen wenn die Datei valide ist.</param>
    /// <param name="logger">Optional Logger fuer Diagnostik.</param>
    /// <returns>True wenn Bootstrap erfolgreich angewendet wurde.</returns>
    public static bool TryApply(
        string pluginDirectory,
        Action<string, string> apply,
        ILogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(apply);

        if (string.IsNullOrWhiteSpace(pluginDirectory))
        {
            return false;
        }

        var path = Path.Combine(pluginDirectory, FileName);
        if (!File.Exists(path))
        {
            return false;
        }

        try
        {
            var json = File.ReadAllText(path);
            var data = JsonSerializer.Deserialize<BootstrapData>(json, JsonOptions);

            if (data is null
                || string.IsNullOrWhiteSpace(data.ApiUrl)
                || string.IsNullOrWhiteSpace(data.ApiToken))
            {
                throw new InvalidDataException(
                    "bootstrap.json missing required fields (apiUrl, apiToken)");
            }

            apply(data.ApiUrl, data.ApiToken);

            File.Delete(path);
            logger?.LogInformation(
                "Applied openmedia bootstrap.json (apiUrl={ApiUrl})", data.ApiUrl);
            return true;
        }
        catch (Exception ex)
        {
            logger?.LogWarning(
                ex, "Failed to apply openmedia bootstrap.json — renaming to .invalid");

            try
            {
                var invalid = path + ".invalid";
                if (File.Exists(invalid))
                {
                    File.Delete(invalid);
                }
                File.Move(path, invalid);
            }
            catch (Exception moveEx)
            {
                logger?.LogWarning(
                    moveEx, "Failed to rename openmedia bootstrap.json to .invalid");
            }

            return false;
        }
    }

    private sealed class BootstrapData
    {
        [JsonPropertyName("apiUrl")]
        public string? ApiUrl { get; set; }

        [JsonPropertyName("apiToken")]
        public string? ApiToken { get; set; }
    }
}

using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using Jellyfin.Plugin.OpenMedia.Configuration;
using MediaBrowser.Common.Configuration;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.OpenMedia.Configuration;

/// <summary>
/// Liest eine optionale <c>bootstrap.json</c> und uebernimmt
/// <c>{apiUrl, apiToken}</c> in die Plugin-Konfiguration — aber nur fuer
/// Felder die noch nicht gesetzt sind (Idempotenz: User-Anpassungen bleiben erhalten).
/// Datei wird nach dem Lesen geloescht, auch bei Parse-Fehlern.
/// </summary>
public static class BootstrapReader
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
    /// Sucht <c>bootstrap.json</c> im Plugin-Verzeichnis und uebernimmt die Werte
    /// in <paramref name="config"/>, sofern die Felder dort noch leer sind.
    /// </summary>
    /// <param name="paths">Jellyfin-ApplicationPaths (fuer PluginConfigurationsPath).</param>
    /// <param name="config">Die zu befuellende Plugin-Konfiguration.</param>
    /// <param name="pluginDllDirectory">Verzeichnis der Plugin-DLL (alternativer Suchort).</param>
    /// <param name="logger">Optional Logger fuer Diagnostik.</param>
    /// <returns>True wenn Bootstrap-Werte erfolgreich angewendet wurden.</returns>
    public static bool TryApply(
        IApplicationPaths paths,
        PluginConfiguration config,
        string? pluginDllDirectory = null,
        ILogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(config);

        // Suche bootstrap.json: prioritaet PluginConfigurationsPath, Fallback DLL-Verzeichnis
        var bootstrapPath = FindBootstrapFile(paths, pluginDllDirectory);
        if (bootstrapPath is null)
        {
            return false;
        }

        try
        {
            var json = File.ReadAllText(bootstrapPath);
            var data = JsonSerializer.Deserialize<BootstrapData>(json, JsonOptions);

            if (data is null
                || string.IsNullOrWhiteSpace(data.ApiUrl)
                || string.IsNullOrWhiteSpace(data.ApiToken))
            {
                logger?.LogWarning(
                    "openmedia bootstrap.json present but missing required fields (apiUrl, apiToken)");
                DeleteFile(bootstrapPath, logger);
                return false;
            }

            // Idempotenz: nur leere Felder befuellen
            bool applied = false;

            if (string.IsNullOrWhiteSpace(config.ApiUrl))
            {
                config.ApiUrl = data.ApiUrl;
                applied = true;
            }
            else
            {
                logger?.LogInformation(
                    "openmedia bootstrap.json: ApiUrl already configured — skipping");
            }

            if (string.IsNullOrWhiteSpace(config.ApiToken))
            {
                config.ApiToken = data.ApiToken;
                applied = true;
            }
            else
            {
                logger?.LogInformation(
                    "openmedia bootstrap.json: ApiToken already configured — skipping");
            }

            // MediaSigningSecret: idempotent, nur setzen wenn config noch leer
            if (!string.IsNullOrWhiteSpace(data.MediaSigningSecret)
                && string.IsNullOrWhiteSpace(config.MediaSigningSecret))
            {
                config.MediaSigningSecret = data.MediaSigningSecret;
                applied = true;
                logger?.LogInformation(
                    "openmedia bootstrap.json: MediaSigningSecret set (length={Len})",
                    data.MediaSigningSecret.Length);
            }
            else if (!string.IsNullOrWhiteSpace(data.MediaSigningSecret))
            {
                logger?.LogInformation(
                    "openmedia bootstrap.json: MediaSigningSecret already configured — skipping");
            }

            if (applied)
            {
                // Token-Prefix loggen (ohne den ganzen Token)
                var tokenPrefix = data.ApiToken.Length > 6
                    ? data.ApiToken[..6] + "…"
                    : "om_…";
                logger?.LogInformation(
                    "openmedia bootstrap.json applied: apiUrl={ApiUrl}, apiToken={TokenPrefix}",
                    data.ApiUrl,
                    tokenPrefix);
            }
            else
            {
                logger?.LogInformation(
                    "openmedia bootstrap.json: config already set — skipping entirely");
            }

            DeleteFile(bootstrapPath, logger);
            return applied;
        }
        catch (JsonException ex)
        {
            logger?.LogWarning(
                ex, "openmedia bootstrap.json: JSON parse error — deleting file");
            DeleteFile(bootstrapPath, logger);
            return false;
        }
        catch (Exception ex)
        {
            logger?.LogWarning(
                ex, "openmedia bootstrap.json: unexpected error — deleting file");
            DeleteFile(bootstrapPath, logger);
            return false;
        }
    }

    /// <summary>
    /// Sucht bootstrap.json in zwei moeglichen Verzeichnissen.
    /// Gibt den Pfad zurueck wenn die Datei existiert, sonst null.
    /// </summary>
    internal static string? FindBootstrapFile(IApplicationPaths paths, string? pluginDllDirectory)
    {
        // Prim: PluginConfigurationsPath (dorthin entpackt Jellyfin Plugin-ZIPs)
        if (!string.IsNullOrWhiteSpace(paths.PluginConfigurationsPath))
        {
            var configPath = Path.Combine(paths.PluginConfigurationsPath, FileName);
            if (File.Exists(configPath))
            {
                return configPath;
            }
        }

        // Fallback: Verzeichnis der Plugin-DLL
        if (!string.IsNullOrWhiteSpace(pluginDllDirectory))
        {
            var dllPath = Path.Combine(pluginDllDirectory, FileName);
            if (File.Exists(dllPath))
            {
                return dllPath;
            }
        }

        return null;
    }

    private static void DeleteFile(string path, ILogger? logger)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception ex)
        {
            logger?.LogWarning(
                ex, "openmedia bootstrap.json: failed to delete {Path}", path);
        }
    }

    private sealed class BootstrapData
    {
        [JsonPropertyName("apiUrl")]
        public string? ApiUrl { get; set; }

        [JsonPropertyName("apiToken")]
        public string? ApiToken { get; set; }

        [JsonPropertyName("media_signing_secret")]
        public string? MediaSigningSecret { get; set; }
    }
}

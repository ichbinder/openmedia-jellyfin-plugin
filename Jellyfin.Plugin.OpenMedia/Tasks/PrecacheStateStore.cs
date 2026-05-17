using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.OpenMedia.Tasks;

/// <summary>
/// Persistenter State fuer den Pre-Cache-Worker. Ueberlebt Plugin-Restarts
/// und verhindert .partial-File-Leaks. Single Source of Truth fuer
/// StrmSyncEngine-Race-Vermeidung.
/// </summary>
public sealed class PrecacheStateStore : IDisposable
{
    /// <summary>Schema-Version fuer zukuenftige Migrationen.</summary>
    private const int SchemaVersion = 1;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly string _filePath;
    private readonly ILogger _logger;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private bool _disposed;

    /// <summary>
    /// Erzeugt einen neuen Store. <paramref name="dataPath"/> ist typischerweise
    /// <c>IApplicationPaths.DataPath</c>.
    /// </summary>
    public PrecacheStateStore(string dataPath, ILogger<PrecacheStateStore> logger)
    {
        ArgumentNullException.ThrowIfNull(dataPath);
        ArgumentNullException.ThrowIfNull(logger);

        var dir = Path.Combine(dataPath, "openmedia");
        Directory.CreateDirectory(dir);
        _filePath = Path.Combine(dir, "precache-state.json");
        _logger = logger;
    }

    /// <summary>
    /// Gibt alle Eintraege zurueck. Liefert eine leere Sammlung wenn keine State-Datei existiert.
    /// </summary>
    public async Task<IReadOnlyDictionary<string, PrecacheEntry>> GetAllAsync(CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        var doc = await ReadStateAsync(ct).ConfigureAwait(false);
        return doc.Entries is null
            ? new Dictionary<string, PrecacheEntry>(StringComparer.Ordinal)
            : new Dictionary<string, PrecacheEntry>(doc.Entries, StringComparer.Ordinal);
    }

    /// <summary>
    /// Gibt einen einzelnen Eintrag zurueck, oder null wenn nicht vorhanden.
    /// </summary>
    public async Task<PrecacheEntry?> GetAsync(string hash, CancellationToken ct)
    {
        var all = await GetAllAsync(ct).ConfigureAwait(false);
        return all.TryGetValue(hash, out var entry) ? entry : null;
    }

    /// <summary>
    /// Load-modify-write mit File-Lock. Der <paramref name="mutator"/> erhaelt den
    /// aktuellen Eintrag (oder null) und muss den gewuenschten neuen Eintrag zurueckgeben.
    /// </summary>
    public async Task UpdateAsync(string hash, Func<PrecacheEntry?, PrecacheEntry> mutator, CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(hash);
        ArgumentNullException.ThrowIfNull(mutator);

        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var doc = await ReadStateAsync(ct).ConfigureAwait(false);
            doc.Entries ??= new Dictionary<string, PrecacheEntry>(StringComparer.Ordinal);

            var current = doc.Entries.TryGetValue(hash, out var existing) ? existing : null;
            var updated = mutator(current);

            if (updated is not null)
            {
                updated.LastEventAt = DateTime.UtcNow;
                doc.Entries[hash] = updated;
            }
            else
            {
                // Mutator returned null → remove entry
                doc.Entries.Remove(hash);
            }

            await WriteStateAsync(doc, ct).ConfigureAwait(false);
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>
    /// Entfernt einen Eintrag asynchron.
    /// </summary>
    public async Task RemoveAsync(string hash, CancellationToken ct)
    {
        await UpdateAsync(hash, _ => null, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Prueft ob ein Hash als cached gilt: state==done UND die lokale Datei existiert.
    /// Single Source of Truth gegen Race-Conditions mit StrmSyncEngine.
    /// </summary>
    public async Task<bool> IsCachedAsync(string hash, CancellationToken ct)
    {
        var entry = await GetAsync(hash, ct).ConfigureAwait(false);
        if (entry is null || entry.State != PrecacheState.Done)
        {
            return false;
        }

        // File existence check — wenn state=done aber Datei fehlt, ist es nicht cached
        if (string.IsNullOrEmpty(entry.LocalPath) || !File.Exists(entry.LocalPath))
        {
            return false;
        }

        return true;
    }

    private async Task<StateDocument> ReadStateAsync(CancellationToken ct)
    {
        if (!File.Exists(_filePath))
        {
            return new StateDocument { SchemaVersion = SchemaVersion };
        }

        try
        {
            var json = await File.ReadAllTextAsync(_filePath, ct).ConfigureAwait(false);
            var doc = JsonSerializer.Deserialize<StateDocument>(json, JsonOptions);
            return doc ?? new StateDocument { SchemaVersion = SchemaVersion };
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "precache:state_corrupt — backing up and resetting");

            // Backup corrupted file
            var backupPath = $"{_filePath}.corrupt-{DateTimeOffset.UtcNow.ToUnixTimeSeconds()}";
            try
            {
                File.Move(_filePath, backupPath);
                _logger.LogInformation("precache:state_backup {BackupPath}", backupPath);
            }
            catch (Exception moveEx)
            {
                _logger.LogWarning(moveEx, "precache:state_backup_failed");
            }

            return new StateDocument { SchemaVersion = SchemaVersion };
        }
    }

    private async Task WriteStateAsync(StateDocument doc, CancellationToken ct)
    {
        doc.SchemaVersion = SchemaVersion;

        // Atomic write: temp file + File.Move
        var tmpPath = _filePath + ".tmp";
        try
        {
            var json = JsonSerializer.Serialize(doc, JsonOptions);
            await File.WriteAllTextAsync(tmpPath, json, ct).ConfigureAwait(false);
            File.Move(tmpPath, _filePath, overwrite: true);

            _logger.LogDebug("precache:state_persisted {Entries}", doc.Entries?.Count ?? 0);
        }
        catch
        {
            // Cleanup temp file on failure
            try { if (File.Exists(tmpPath)) File.Delete(tmpPath); } catch { /* best effort */ }
            throw;
        }
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            _lock.Dispose();
            _disposed = true;
        }
    }
}

/// <summary>
/// Persistenz-State eines einzelnen Pre-Cache-Eintrags.
/// </summary>
public sealed class PrecacheEntry
{
    [JsonPropertyName("state")]
    public PrecacheState State { get; set; }

    [JsonPropertyName("downloadedBytes")]
    public long DownloadedBytes { get; set; }

    [JsonPropertyName("sizeBytes")]
    public long SizeBytes { get; set; }

    [JsonPropertyName("sha256")]
    public string? Sha256 { get; set; }

    [JsonPropertyName("localPath")]
    public string? LocalPath { get; set; }

    [JsonPropertyName("lastError")]
    public string? LastError { get; set; }

    [JsonPropertyName("ttlSeconds")]
    public int? TtlSeconds { get; set; }

    [JsonPropertyName("lastEventAt")]
    public DateTime LastEventAt { get; set; }
}

/// <summary>
/// State-Werte eines Pre-Cache-Eintrags.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<PrecacheState>))]
public enum PrecacheState
{
    Pending,
    Downloading,
    Verifying,
    Done,
    Failed
}

/// <summary>
/// Top-Level JSON-Document fuer precache-state.json.
/// </summary>
internal sealed class StateDocument
{
    [JsonPropertyName("schemaVersion")]
    public int SchemaVersion { get; set; } = 1;

    [JsonPropertyName("entries")]
    public Dictionary<string, PrecacheEntry>? Entries { get; set; }
}

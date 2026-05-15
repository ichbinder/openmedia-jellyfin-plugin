# openmedia Jellyfin Plugin

Bindet die persoenliche openmedia-UserLibrary als virtuelle Jellyfin-Library ein.
Filme werden direkt aus S3 gestreamt (Direct-Play). Primaerer Test-Client: Apple TV / Swiftfin.

- Plugin-GUID: `8cfc3c6a-c39f-467f-8ebe-9f3218724aa1`
- Target: Jellyfin **10.11+** (`net9.0`, `Jellyfin.Controller` 10.11.x)
- Status: **M041/S03** — Bootstrap-Reader (1.1.0)

## Lokaler Dev-Loop (macOS)

```bash
# Build
dotnet publish Jellyfin.Plugin.OpenMedia/Jellyfin.Plugin.OpenMedia.csproj \
  -c Release -o ./publish

# Deploy in den nativen Jellyfin
PLUGIN_DIR="$HOME/Library/Application Support/jellyfin/plugins/OpenMedia_1.0.0.0"
mkdir -p "$PLUGIN_DIR"
cp ./publish/Jellyfin.Plugin.OpenMedia.dll "$PLUGIN_DIR/"

# Jellyfin neu starten (App schliessen + erneut oeffnen)
# Logs: ~/Library/Application Support/jellyfin/log/log_*.log
```

Nach dem Restart: **Admin-Dashboard -> Plugins -> openmedia** -> Settings-Tab.

## Auto-Konfiguration via `bootstrap.json`

Beim ersten Start sucht das Plugin nach einer Datei `bootstrap.json` —
zuerst im Plugin-Konfigurationsverzeichnis
(`<Jellyfin-Config>/plugins/configurations/`), danach im DLL-Verzeichnis
des Plugins. Existiert die Datei, werden `ApiUrl` und `ApiToken`
automatisch in die Plugin-Configuration uebernommen, sodass der User die
Settings nicht mehr manuell ausfuellen muss.

Format:

```json
{
  "apiUrl": "https://api.mediatoken.de",
  "apiToken": "om_xxxxxxxxxxxxxxxx"
}
```

Verhalten:

- **Idempotent:** Nur leere Config-Felder werden gefuellt. Bereits gesetzte
  User-Werte bleiben unangetastet.
- **Einmal-Konsum:** `bootstrap.json` wird nach dem Lesen geloescht — auch
  bei Parse-Fehlern. Die Datei wird also genau einmal verarbeitet.
- **Logging:** Plugin-Log enthaelt einen Eintrag mit Token-Prefix (ohne
  vollstaendigen Token), wenn die Datei gelesen wird, sowie einen
  Skip-Eintrag, wenn die Config bereits gesetzt ist.

## Naechste Slices

- **S02:** API-Endpoints `/jellyfin/library` + `/jellyfin/stream/:hash` in openmedia-api
- **S03:** Library-Sync (`IScheduledTask`) + `IMediaSourceProvider`
- **S04:** Apple TV / Swiftfin End-to-End-Test
- **S05:** Distribution: `manifest.json` + Plugin-Catalog-URL

Die GSD-Planung lebt in `~/git/movie-test-02/.gsd/milestones/M039/`.

# Changelog

Alle bemerkenswerten Änderungen an diesem Plugin werden in dieser Datei dokumentiert.

Das Format basiert auf [Keep a Changelog](https://keepachangelog.com/de/1.1.0/)
und das Projekt folgt [Semantic Versioning](https://semver.org/lang/de/).

## [1.1.0] - 2026-05-14

### Hinzugefügt
- `BootstrapReader`: liest beim ersten Start eine `bootstrap.json` aus dem
  Plugin-Konfigurationsverzeichnis (oder neben den DLLs) und schreibt
  `ApiUrl` + `ApiToken` in die Plugin-Configuration. Damit kann das Plugin
  vorkonfiguriert ausgeliefert werden, ohne dass der User die Settings öffnen
  muss.
- Idempotenz: Nur leere Config-Felder werden befüllt — bestehende
  User-Anpassungen bleiben erhalten.
- Delete-Semantik: `bootstrap.json` wird nach dem Lesen immer gelöscht
  (auch bei Parse-Fehler), damit die Datei genau einmal konsumiert wird.
- Smoke-Test-Skript `scripts/smoke-bootstrap.sh` für den lokalen
  End-to-End-Test gegen das native macOS Jellyfin.

### Geändert
- `BootstrapLoader` durch `BootstrapReader` ersetzt. Die neue API nutzt
  `IApplicationPaths` + `PluginConfiguration` direkt statt eines
  Callback-Patterns.

## [1.0.0] - 2026-05-10

### Hinzugefügt
- Initiales Plugin-Skelett mit Settings-Tab.
- Auto-Create der `OpenMedia`-Library beim Plugin-Start.
- STRM-Layout für Filme.
- `LibraryPollingService` mit ETag-basiertem 15s-Polling.
- GitHub-Actions-Release-Workflow mit Distribution über orphan
  `dist`-Branch.

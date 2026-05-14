#!/usr/bin/env bash
# smoke-bootstrap.sh — Smoke-Test fuer den BootstrapReader im lokalen Jellyfin.
#
# Szenarien:
#   1) Frische Installation: Plugin-Dir leer (keine Config-XML), bootstrap.json
#      vorhanden -> nach Restart muessen ApiUrl + ApiToken in der Config-XML
#      stehen und bootstrap.json muss geloescht sein.
#   2) Idempotenz: Config-XML mit manuell geaendertem ApiToken, bootstrap.json
#      mit anderem Wert wieder reinkopieren -> nach Restart bleibt der manuelle
#      ApiToken erhalten, bootstrap.json wird trotzdem geloescht.
#
# Voraussetzungen:
#   - Lokales Jellyfin (nativer .pkg-Build) installiert.
#   - Plugin-DLL gebaut: dotnet build -c Release (Version 1.1.0.0).

set -euo pipefail

PLUGIN_NAME="OpenMedia"
PLUGIN_VERSION="1.1.0.0"
PLUGIN_GUID_FILE="Jellyfin.Plugin.OpenMedia.xml"

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
DLL_SRC="${REPO_ROOT}/Jellyfin.Plugin.OpenMedia/bin/Release/net9.0/Jellyfin.Plugin.OpenMedia.dll"

JELLYFIN_DATA="${HOME}/Library/Application Support/jellyfin"
PLUGIN_DIR="${JELLYFIN_DATA}/plugins/${PLUGIN_NAME}_${PLUGIN_VERSION}"
CONFIG_DIR="${JELLYFIN_DATA}/plugins/configurations"
CONFIG_XML="${CONFIG_DIR}/${PLUGIN_GUID_FILE}"
LOG_DIR="${JELLYFIN_DATA}/log"

JELLYFIN_APP="/Applications/Jellyfin.app"
JELLYFIN_BIN="${JELLYFIN_APP}/Contents/MacOS/jellyfin"
JELLYFIN_LAUNCHER="${JELLYFIN_APP}/Contents/MacOS/Jellyfin Server"
HEALTH_URL="http://localhost:8096/System/Info/Public"

EXPECT_API_URL="https://api.mediatoken.de"
EXPECT_API_TOKEN="om_smoke_token_aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"
MANUAL_API_TOKEN="om_user_edited_token_xxxxxxxxxxxxxxxxxxxxxxxxxxxxxx"
SECOND_API_TOKEN="om_should_not_overwrite_yyyyyyyyyyyyyyyyyyyyyyyyyy"

log() { printf '[smoke] %s\n' "$*"; }
fail() { printf '[smoke][FAIL] %s\n' "$*" >&2; exit 1; }

require_file() {
  [[ -f "$1" ]] || fail "missing file: $1"
}

stop_jellyfin() {
  local stopped=0
  # Backend zuerst (sauberer Shutdown via Launcher haengt die DB)
  if pgrep -f "${JELLYFIN_BIN}" >/dev/null 2>&1; then
    log "stopping Jellyfin backend..."
    pkill -f "${JELLYFIN_BIN}" || true
    stopped=1
  fi
  # Launcher killen — startet sonst Backend nicht neu
  if pgrep -f "${JELLYFIN_LAUNCHER}" >/dev/null 2>&1; then
    log "stopping Jellyfin launcher..."
    pkill -f "${JELLYFIN_LAUNCHER}" || true
    stopped=1
  fi
  if [[ ${stopped} -eq 1 ]]; then
    for _ in {1..60}; do
      if ! pgrep -f "${JELLYFIN_BIN}" >/dev/null 2>&1 \
        && ! pgrep -f "${JELLYFIN_LAUNCHER}" >/dev/null 2>&1 \
        && ! curl -fsS "${HEALTH_URL}" >/dev/null 2>&1; then
        return 0
      fi
      sleep 0.5
    done
    fail "Jellyfin did not stop"
  fi
}

start_jellyfin() {
  log "starting Jellyfin..."
  open -ga "${JELLYFIN_APP}"
  # auf API warten
  for _ in {1..90}; do
    if curl -fsS "${HEALTH_URL}" >/dev/null 2>&1; then
      log "Jellyfin healthy"
      return 0
    fi
    sleep 1
  done
  fail "Jellyfin did not become healthy"
}

extract_xml_field() {
  # $1 = file, $2 = element-name (e.g. ApiToken)
  python3 -c "
import sys, xml.etree.ElementTree as ET
root = ET.parse(sys.argv[1]).getroot()
el = root.find(sys.argv[2])
print(el.text if el is not None and el.text is not None else '')
" "$1" "$2"
}

set_xml_field() {
  # $1 = file, $2 = element, $3 = value
  python3 -c "
import sys, xml.etree.ElementTree as ET
tree = ET.parse(sys.argv[1])
root = tree.getroot()
el = root.find(sys.argv[2])
if el is None:
    raise SystemExit(f'element {sys.argv[2]} not found')
el.text = sys.argv[3]
tree.write(sys.argv[1], xml_declaration=True, encoding='utf-8')
" "$1" "$2" "$3"
}

write_bootstrap() {
  # $1 = token
  cat > "${PLUGIN_DIR}/bootstrap.json" <<EOF
{"apiUrl":"${EXPECT_API_URL}","apiToken":"$1"}
EOF
}

require_file "${DLL_SRC}"

log "=== Scenario 1: fresh install — bootstrap.json -> config ==="
stop_jellyfin

# Plugin-Dir frisch aufbauen, alte Config-XML wegraeumen
rm -rf "${PLUGIN_DIR}"
mkdir -p "${PLUGIN_DIR}"
cp "${DLL_SRC}" "${PLUGIN_DIR}/"

# alte Config-XML weg, damit Plugin Felder als leer sieht
if [[ -f "${CONFIG_XML}" ]]; then
  mv "${CONFIG_XML}" "${CONFIG_XML}.bak-smoke"
fi

write_bootstrap "${EXPECT_API_TOKEN}"
log "bootstrap.json written with token prefix=${EXPECT_API_TOKEN:0:8}"

start_jellyfin

# Jellyfin braucht einen kurzen Moment, um Plugin zu laden und Config zu speichern
for _ in {1..30}; do
  [[ -f "${CONFIG_XML}" ]] && break
  sleep 1
done
[[ -f "${CONFIG_XML}" ]] || fail "config XML was not created"

GOT_URL="$(extract_xml_field "${CONFIG_XML}" ApiUrl)"
GOT_TOKEN="$(extract_xml_field "${CONFIG_XML}" ApiToken)"
[[ "${GOT_URL}" == "${EXPECT_API_URL}" ]] \
  || fail "ApiUrl mismatch: got='${GOT_URL}' want='${EXPECT_API_URL}'"
[[ "${GOT_TOKEN}" == "${EXPECT_API_TOKEN}" ]] \
  || fail "ApiToken mismatch (scenario 1): got prefix='${GOT_TOKEN:0:12}' want prefix='${EXPECT_API_TOKEN:0:12}'"
[[ ! -f "${PLUGIN_DIR}/bootstrap.json" ]] \
  || fail "bootstrap.json was not deleted after first read"

log "Scenario 1 PASS — ApiUrl + ApiToken populated, bootstrap.json deleted"

log "=== Scenario 2: idempotent — user-edited token must survive ==="
stop_jellyfin

# Token manuell aendern (simuliert User-Edit im Dashboard)
set_xml_field "${CONFIG_XML}" ApiToken "${MANUAL_API_TOKEN}"

# bootstrap.json wieder reinlegen mit anderem Token
write_bootstrap "${SECOND_API_TOKEN}"
log "user-edited token set, bootstrap.json re-written with different token"

start_jellyfin

# Bootstrap.json sollte trotzdem geloescht werden — kurz warten
for _ in {1..15}; do
  [[ -f "${PLUGIN_DIR}/bootstrap.json" ]] || break
  sleep 1
done

GOT_TOKEN2="$(extract_xml_field "${CONFIG_XML}" ApiToken)"
[[ "${GOT_TOKEN2}" == "${MANUAL_API_TOKEN}" ]] \
  || fail "ApiToken was overwritten (scenario 2): got prefix='${GOT_TOKEN2:0:12}' want prefix='${MANUAL_API_TOKEN:0:12}'"
[[ ! -f "${PLUGIN_DIR}/bootstrap.json" ]] \
  || fail "bootstrap.json was not deleted in idempotent scenario"

log "Scenario 2 PASS — user-edited token preserved, bootstrap.json still deleted"

log "=== Log evidence ==="
# Letzte BootstrapReader-Logeintraege ausgeben (Diagnose)
LATEST_LOG="$(ls -t "${LOG_DIR}"/log_*.log 2>/dev/null | head -1 || true)"
if [[ -n "${LATEST_LOG}" ]]; then
  log "scanning ${LATEST_LOG} for bootstrap entries..."
  grep -i "bootstrap" "${LATEST_LOG}" | tail -20 || log "(no bootstrap entries in current log file)"
fi

# Backup der originalen Config-XML wiederherstellen, falls vorhanden
if [[ -f "${CONFIG_XML}.bak-smoke" ]]; then
  log "restoring original config XML"
  mv -f "${CONFIG_XML}.bak-smoke" "${CONFIG_XML}"
  # Jellyfin neu starten, damit es die echte Config laedt
  stop_jellyfin
  start_jellyfin
fi

log "ALL SCENARIOS PASS"

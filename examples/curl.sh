#!/usr/bin/env bash
# Worked example: drive SboxServerConsole from curl. Set HOST and RCON_PASSWORD to match
# your install. All authenticated calls accept either:
#   - X-RCON-Password header (preferred)
#   - ?password=... query string (fallback for browser EventSource)

HOST="${HOST:-http://127.0.0.1:27019}"
# Not PWD: the shell always sets that to the current directory, so the guard
# below would never fire and every call would authenticate with a path.
RCON_PASSWORD="${RCON_PASSWORD:?set RCON_PASSWORD to your --rcon-password}"
H="X-RCON-Password: $RCON_PASSWORD"

# Health (no auth)
curl -s "$HOST/health" | jq .

# Recent log lines
curl -s -H "$H" "$HOST/history?count=50" | jq '.entries[-5:]'

# Send a command, return output collected within 250ms
curl -s -H "$H" -H 'Content-Type: application/json' \
  -d '{"cmd":"status"}' "$HOST/execute?collect=1" | jq .

# Add a ban
curl -s -H "$H" -H 'Content-Type: application/json' \
  -d '{"steamid":"76561198000000001","reason":"griefing"}' \
  "$HOST/bans" | jq .

# Add a recurring scheduled command (every 30 minutes). The id is optional;
# without one the agent makes one up and returns it.
curl -s -H "$H" -H 'Content-Type: application/json' \
  -d '{"id":"status-check","schedule":"@every 30m","command":"status"}' \
  "$HOST/scheduler" | jq .

# Disable the job (keeps the entry but stops firing). Send a body: Windows
# rejects an empty POST without Content-Length.
curl -s -X POST -H "$H" -d '{}' "$HOST/scheduler/status-check/disable" | jq .

# Live tail (SSE). Runs until you press Ctrl-C, so it goes last.
curl -N -H "$H" "$HOST/stream"

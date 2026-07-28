#!/usr/bin/env bash
# Worked example: drive SboxServerConsole from curl. Set HOST and RCON_PASSWORD to match
# your install. All authenticated calls accept either:
#   - X-RCON-Password header (preferred)
#   - ?password=... query string (fallback for browser EventSource)

HOST="${HOST:-http://127.0.0.1:27019}"
# Not PWD — the shell always sets that to the current directory, so the guard
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

# Live tail (SSE)
curl -N -H "$H" "$HOST/stream"

# Add a ban
curl -s -H "$H" -H 'Content-Type: application/json' \
  -d '{"steamid":"76561198000000001","reason":"griefing"}' \
  "$HOST/bans" | jq .

# Add a recurring scheduled command (announce every 30 minutes)
curl -s -H "$H" -H 'Content-Type: application/json' \
  -d '{"id":"hourly-msg","schedule":"@every 30m","command":"say Server restarts in 4h"}' \
  "$HOST/scheduler" | jq .

# Disable the job (keeps the entry but stops firing)
curl -s -X POST -H "$H" "$HOST/scheduler/hourly-msg/disable" | jq .

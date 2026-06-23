#!/usr/bin/env bash
set -euo pipefail

# Runs a complete local demonstration, then stops both servers. The hardcoded API
# order, its real NServiceBus handler activity, and its EventFlow trace all use order-10042.

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"

if [[ -n "${DOTNET:-}" ]]; then
  DOTNET="$DOTNET"
elif [[ -x "$HOME/.dotnet/dotnet" ]]; then
  # The official dotnet-install script's default per-user location.
  DOTNET="$HOME/.dotnet/dotnet"
elif command -v dotnet >/dev/null 2>&1; then
  DOTNET="$(command -v dotnet)"
elif [[ -x "/usr/local/share/dotnet/dotnet" ]]; then
  DOTNET="/usr/local/share/dotnet/dotnet"
else
  echo "No .NET SDK was found. Install the .NET 8 SDK, or rerun with DOTNET=/path/to/dotnet." >&2
  exit 1
fi
API_URL="http://127.0.0.1:5099"
MCP_URL="http://127.0.0.1:5100"
MCP_KEY="local-demo-key-only-not-for-shared-use-2026"
LOG_DIR="${TMPDIR:-/tmp}/eventflow-local-demo-$$"
PROJECTION_PATH="$LOG_DIR/nservicebus-demo.operations.json"
API_PID=""
MCP_PID=""

cleanup() {
  for pid in "$MCP_PID" "$API_PID"; do
    if [[ -n "$pid" ]]; then
      kill "$pid" 2>/dev/null || true
      for _ in $(seq 1 25); do
        if ! kill -0 "$pid" 2>/dev/null; then
          wait "$pid" 2>/dev/null || true
          break
        fi
        sleep 0.2
      done
      if kill -0 "$pid" 2>/dev/null; then
        kill -9 "$pid" 2>/dev/null || true
        wait "$pid" 2>/dev/null || true
      fi
    fi
  done
}
trap cleanup EXIT INT TERM

mkdir -p "$LOG_DIR"

wait_for() {
  local url="$1"
  local log_file="$2"

  for _ in $(seq 1 120); do
    if curl --silent --show-error --fail "$url" >/dev/null 2>&1; then
      return 0
    fi
    sleep 0.25
  done

  echo "Timed out waiting for $url. Log: $log_file" >&2
  sed -n '1,240p' "$log_file" >&2 || true
  return 1
}

if [[ "${SKIP_BUILD:-}" != "1" ]]; then
  echo "Building the demo API and EventFlow server..."
  "$DOTNET" build "$ROOT/samples/NServiceBusDemo.Api/NServiceBusDemo.Api.csproj" --configuration Release --disable-build-servers --nologo >/dev/null
  "$DOTNET" build "$ROOT/src/EventFlowMcp.Server/EventFlowMcp.Server.csproj" --configuration Release --disable-build-servers --nologo >/dev/null
fi

echo "Starting NServiceBus demo API at $API_URL..."
ASPNETCORE_URLS="$API_URL" \
  EventFlow__ProjectionPath="$PROJECTION_PATH" \
  "$DOTNET" "$ROOT/samples/NServiceBusDemo.Api/bin/Release/net8.0/NServiceBusDemo.Api.dll" \
  >"$LOG_DIR/nservicebus-api.log" 2>&1 &
API_PID=$!
wait_for "$API_URL/healthz" "$LOG_DIR/nservicebus-api.log"

echo "Submitting and approving hardcoded order-10042 through NServiceBus..."
curl --silent --show-error --fail -X POST "$API_URL/orders/order-10042/submit" >/dev/null
for _ in $(seq 1 80); do
  order="$(curl --silent --show-error --fail "$API_URL/orders/order-10042")"
  [[ "$order" == *'"status":"Submitted"'* ]] && break
  sleep 0.1
done
[[ "$order" == *'"status":"Submitted"'* ]]

curl --silent --show-error --fail -X POST "$API_URL/orders/order-10042/approve" >/dev/null
for _ in $(seq 1 80); do
  order="$(curl --silent --show-error --fail "$API_URL/orders/order-10042")"
  [[ "$order" == *'"status":"Approved"'* ]] && break
  sleep 0.1
done
[[ "$order" == *'"status":"Approved"'* ]]
echo "NServiceBus demo result: $order"

echo "Starting EventFlow MCP server at $MCP_URL..."
ASPNETCORE_URLS="$MCP_URL" \
  Mcp__ApiKey="$MCP_KEY" \
  Operations__Provider=LocalProjection \
  Operations__LocalProjection__Path="$PROJECTION_PATH" \
  Rag__Provider=InMemory \
  "$DOTNET" "$ROOT/src/EventFlowMcp.Server/bin/Release/net8.0/EventFlowMcp.Server.dll" \
  >"$LOG_DIR/eventflow-mcp.log" 2>&1 &
MCP_PID=$!
wait_for "$MCP_URL/readyz" "$LOG_DIR/eventflow-mcp.log"

mcp_post() {
  curl --silent --show-error --fail \
    -X POST "$MCP_URL/mcp" \
    -H "X-MCP-API-Key: $MCP_KEY" \
    -H "Content-Type: application/json" \
    -H "Accept: application/json, text/event-stream" \
    -H "MCP-Protocol-Version: 2025-03-26" \
    --data "$1"
}

echo "Initializing MCP and listing tools..."
initialize="$(mcp_post '{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-03-26","capabilities":{},"clientInfo":{"name":"eventflow-local-demo","version":"1.0"}}}')"
[[ "$initialize" == *'"serverInfo"'* ]]
mcp_post '{"jsonrpc":"2.0","method":"notifications/initialized","params":{}}' >/dev/null || true
tools="$(mcp_post '{"jsonrpc":"2.0","id":2,"method":"tools/list","params":{}}')"

if [[ "$tools" == *'trace_business_process'* ]]; then
  trace_tool="trace_business_process"
elif [[ "$tools" == *'TraceBusinessProcess'* ]]; then
  trace_tool="TraceBusinessProcess"
else
  echo "The expected trace MCP tool was not advertised." >&2
  exit 1
fi

if [[ "$tools" == *'find_saga_instances'* ]]; then
  saga_tool="find_saga_instances"
elif [[ "$tools" == *'FindSagaInstances'* ]]; then
  saga_tool="FindSagaInstances"
else
  echo "The expected saga MCP tool was not advertised." >&2
  exit 1
fi

trace="$(mcp_post "{\"jsonrpc\":\"2.0\",\"id\":3,\"method\":\"tools/call\",\"params\":{\"name\":\"$trace_tool\",\"arguments\":{\"identifier\":\"order-10042\"}}}")"
[[ "$trace" == *'SubmitOrder'* ]]
[[ "$trace" == *'ApproveOrder'* ]]
[[ "$trace" == *'OrderWorkflowSaga'* ]]

saga="$(mcp_post "{\"jsonrpc\":\"2.0\",\"id\":4,\"method\":\"tools/call\",\"params\":{\"name\":\"$saga_tool\",\"arguments\":{\"correlationValue\":\"order-10042\"}}}")"
[[ "$saga" == *'Completed'* ]]
echo "EventFlow MCP read the real local NServiceBus trace and completed saga for order-10042."
echo "Local demo passed. Servers will now stop. Logs remain in $LOG_DIR"

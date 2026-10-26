#!/usr/bin/env bash
#
# Blocks until the whole Shop stack is ready to be tested.
#
# A cold `docker compose up` is not instant: MSSQL takes 20-30s to accept connections, then
# Product.Api and Customer.Api run EF Core migrations and seed, Order.Api seeds Mongo, and
# Keycloak imports the realm. Starting the suite before that finishes produces a wall of
# connection errors that look like test failures.
#
# Usage:
#   ./tests/scripts/wait-for-stack.sh
#   TIMEOUT_SECONDS=300 ./tests/scripts/wait-for-stack.sh

set -euo pipefail

GATEWAY_URL="${GATEWAY_URL:-http://localhost:5000}"
PRODUCT_URL="${PRODUCT_URL:-http://localhost:5001}"
CUSTOMER_URL="${CUSTOMER_URL:-http://localhost:5002}"
ORDER_URL="${ORDER_URL:-http://localhost:5003}"
KEYCLOAK_URL="${KEYCLOAK_URL:-http://localhost:8081}"
REALM="${REALM:-shop}"
TIMEOUT_SECONDS="${TIMEOUT_SECONDS:-180}"
POLL_SECONDS="${POLL_SECONDS:-3}"

declare -a NAMES=(gateway-api product-api customer-api order-api keycloak)
declare -a URLS=(
  "${GATEWAY_URL}/health"
  "${PRODUCT_URL}/health"
  "${CUSTOMER_URL}/health"
  "${ORDER_URL}/health"
  "${KEYCLOAK_URL}/realms/${REALM}/.well-known/openid-configuration"
)

is_ready() {
  local code
  code="$(curl -s -o /dev/null -w '%{http_code}' --max-time 5 "$1" 2>/dev/null || echo 000)"
  [[ "$code" == "200" ]]
}

echo "Waiting up to ${TIMEOUT_SECONDS}s for the Shop stack..."

deadline=$(( $(date +%s) + TIMEOUT_SECONDS ))
declare -a remaining_idx=(0 1 2 3 4)

while [[ ${#remaining_idx[@]} -gt 0 ]]; do
  if [[ $(date +%s) -gt $deadline ]]; then
    still=""
    for i in "${remaining_idx[@]}"; do still+="${NAMES[$i]} "; done
    echo ""
    echo "ERROR: timed out after ${TIMEOUT_SECONDS}s. Still not ready: ${still}" >&2
    echo "Try: docker compose ps   and   docker compose logs --tail=50 <service>" >&2
    exit 1
  fi

  declare -a next_idx=()
  for i in "${remaining_idx[@]}"; do
    if is_ready "${URLS[$i]}"; then
      echo "  ready: ${NAMES[$i]}"
    else
      next_idx+=("$i")
    fi
  done

  remaining_idx=("${next_idx[@]+"${next_idx[@]}"}")

  if [[ ${#remaining_idx[@]} -gt 0 ]]; then
    sleep "$POLL_SECONDS"
  fi
done

echo "Stack is ready."

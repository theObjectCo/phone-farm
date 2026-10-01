#!/usr/bin/env bash
# Submit a Monte Carlo pi job. Usage: ./submit.sh [shards] [iterationsPerShard]
set -euo pipefail
cd "$(dirname "$0")"

SHARDS="${1:-16}"
ITER="${2:-20000000}"
URL="${PHONEFARM_URL:-http://localhost:8080}"

curl -sf -F "name=montecarlo-pi" \
  -F "entry=Worker.MonteCarlo.MonteCarloWorker, Worker.MonteCarlo" \
  -F "assembly=@bin/worker-montecarlo/Worker.MonteCarlo.dll" \
  -F "shards=$SHARDS" \
  -F "iterations=$ITER" \
  "$URL/api/jobs"
echo

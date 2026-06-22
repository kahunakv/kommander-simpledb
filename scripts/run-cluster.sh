#!/usr/bin/env bash
set -euo pipefail

cd "$(dirname "$0")/.."
ROOT="$(pwd)"
PUBLISH_DIR="${SIMPLEDB_PUBLISH_DIR:-/tmp/simpledb-cluster-bin}"
DATA_DIR="${SIMPLEDB_DATA_DIR:-/tmp/simpledb-cluster}"
HOST="127.0.0.1"

echo ">> Publishing SimpleDb to ${PUBLISH_DIR}"
dotnet publish src/SimpleDb/SimpleDb.csproj -c Release -o "${PUBLISH_DIR}"
mkdir -p "${DATA_DIR}/node1" "${DATA_DIR}/node2" "${DATA_DIR}/node3"

pids=()
FIFO_DIR=$(mktemp -d)

cleanup() {
    echo
    echo ">> Stopping cluster..."
    for pid in "${pids[@]:-}"; do
        kill -TERM "$pid" 2>/dev/null || true
    done
    for _ in $(seq 1 50); do
        alive=0
        for pid in "${pids[@]:-}"; do
            kill -0 "$pid" 2>/dev/null && alive=1 && break
        done
        [ "$alive" -eq 0 ] && break
        sleep 0.1
    done
    for pid in "${pids[@]:-}"; do
        kill -KILL "$pid" 2>/dev/null || true
        wait "$pid" 2>/dev/null || true
    done
    rm -rf "$FIFO_DIR"
    echo ">> Cluster stopped."
}
trap cleanup EXIT
trap 'exit 130' INT TERM

ENDPOINT_MAP="${HOST}:7101=http://${HOST}:7001,${HOST}:7102=http://${HOST}:7002,${HOST}:7103=http://${HOST}:7003"

start_node() {
    local id=$1 http_port=$2 grpc_port=$3 peers=$4
    local out_fifo="${FIFO_DIR}/node${id}.out" err_fifo="${FIFO_DIR}/node${id}.err"
    mkfifo "$out_fifo" "$err_fifo"
    sed -u "s/^/[node${id}] /" < "$out_fifo" &
    sed -u "s/^/[node${id}] /" < "$err_fifo" >&2 &

    dotnet "${PUBLISH_DIR}/SimpleDb.dll" \
        --NodeId "$id" \
        --Host "$HOST" \
        --HttpPort "$http_port" \
        --GrpcPort "$grpc_port" \
        --Partitions 8 \
        --DataDirectory "${DATA_DIR}/node${id}" \
        --PeerGrpcEndpoints "$peers" \
        --EndpointMap "$ENDPOINT_MAP" \
        > "$out_fifo" 2> "$err_fifo" &
    pids+=("$!")
    echo ">> node${id} started (PID $!) REST :${http_port} gRPC :${grpc_port}"
}

start_node 1 7001 7101 "${HOST}:7102,${HOST}:7103"
start_node 2 7002 7102 "${HOST}:7101,${HOST}:7103"
start_node 3 7003 7103 "${HOST}:7101,${HOST}:7102"

echo
echo ">> Cluster is running. Ctrl+C stops all three nodes."
echo "   REST: http://${HOST}:7001  http://${HOST}:7002  http://${HOST}:7003"
echo "   Example: curl -L -X PUT http://${HOST}:7001/keys/name -H 'Content-Type: application/json' -d '{\"value\":\"Ada\"}'"
echo

wait "${pids[@]}"

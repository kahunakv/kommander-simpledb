# Kommander SimpleDB

A small replicated string key/value database built with .NET 10, Kommander 1.6.5, gRPC, and SQLite.

Each key is mapped by Kommander to one of eight Raft partitions. Every partition elects its own leader, so different keys may have different leaders. A node that receives a REST request for a partition it does not lead responds with `307 Temporary Redirect` to that leader's REST endpoint. The 307 status preserves the method and body of PUT requests.

Writes are serialized as `simpledb.put` commands and committed through `IRaft.ReplicateLogs`. Every node applies committed and restored commands to its local SQLite `values.db`. Kommander's own durable log also uses `SqliteWAL`. Raft node-to-node traffic uses cleartext HTTP/2 gRPC on the local development ports.

## Prerequisites

- .NET SDK 10
- `curl` for the examples

## Run the cluster

```bash
./scripts/run-cluster.sh
```

The script publishes once, starts three nodes, labels each node's logs, and waits in the foreground. Press Ctrl+C to gracefully stop all three processes; any process that does not exit within five seconds is killed. Data persists under `/tmp/simpledb-cluster` by default.

| Node | REST API | Raft gRPC |
| --- | --- | --- |
| 1 | `http://127.0.0.1:7001` | `127.0.0.1:7101` |
| 2 | `http://127.0.0.1:7002` | `127.0.0.1:7102` |
| 3 | `http://127.0.0.1:7003` | `127.0.0.1:7103` |

Override storage locations with `SIMPLEDB_DATA_DIR` and the publish directory with `SIMPLEDB_PUBLISH_DIR`. Remove the data directory for a clean cluster.

## API

Use `curl -L` so requests follow leader redirects:

```bash
curl -L -X PUT http://127.0.0.1:7001/keys/name \
  -H 'Content-Type: application/json' \
  -d '{"value":"Ada"}'

curl -L http://127.0.0.1:7002/keys/name
```

`PUT /keys/{key}` accepts `{ "value": "a string" }`. `GET /keys/{key}` returns the value or `404`. `/health` reports whether that node has initialized its Kommander partitions.

For production, replace cleartext gRPC with TLS and node authentication, use externally reachable advertised addresses, and add snapshot/state-transfer handling before enabling elastic partition splits.

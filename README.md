# Kommander SimpleDB

A small replicated string key/value database built with .NET 10, Kommander 1.14.0, gRPC, and SQLite.

Each key is mapped by Kommander to one of eight Raft partitions. Every partition elects its own leader, so different keys may have different leaders. A node that receives a REST request for a partition it does not lead responds with `307 Temporary Redirect` to that leader's REST endpoint. The 307 status preserves the method and body of PUT requests.

Writes are serialized as `simpledb.put` commands and committed through `IRaft.ReplicateLogs`. Every node applies committed and restored commands to its local SQLite `values.db`. Kommander's own durable log also uses `SqliteWAL`. Raft node-to-node traffic uses cleartext HTTP/2 gRPC on the local development ports.

## Prerequisites

- .NET SDK 10
- `curl` for the examples

## Projects

| Project | What it is |
| --- | --- |
| `src/SimpleDb` | The database node. |
| `src/SimpleDb.ServiceDefaults` | OpenTelemetry (logs, traces, and metrics, with the `Kommander` meter) and the `/alive` endpoint. Without an OTLP endpoint, it exports nothing. |
| `src/SimpleDb.AppHost` | An Aspire AppHost that runs the three nodes as project resources. |

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

## Run the cluster in Aspire

```bash
dotnet run --project src/SimpleDb.AppHost --launch-profile http
```

The AppHost needs no Aspire CLI and no Docker. It starts the same three nodes as the script, as the resources `simpledb-1`, `simpledb-2`, and `simpledb-3`. The output shows the login URL of the dashboard. The dashboard shows the logs, the traces, and the metrics of each node, and you can stop and start each node from it.

The nodes use the same fixed ports as the script, with no Aspire proxy. Raft identifies a node by its gRPC address, and a redirect must name the real REST address of the leader. Thus you cannot run the script and the AppHost at the same time.

Each node is healthy in the dashboard when its `/health` endpoint returns 200, that is, when the node has initialized its partitions. The nodes do not wait for each other, because a node becomes ready only when a majority of nodes runs.

The data persists under `simpledb-aspire` in the system temporary directory (`$TMPDIR` on macOS). To use a different directory, set `SimpleDb:DataDirectory`. To change the number of partitions, set `SimpleDb:Partitions`. For example:

```bash
dotnet run --project src/SimpleDb.AppHost --launch-profile http -- --SimpleDb:DataDirectory=/tmp/simpledb-aspire
```

To see a failover, stop the leader of a key in the dashboard. Then write the key again through one of the other nodes.

## API

Use `curl -L` so requests follow leader redirects:

```bash
curl -L -X PUT http://127.0.0.1:7001/keys/name \
  -H 'Content-Type: application/json' \
  -d '{"value":"Ada"}'

curl -L http://127.0.0.1:7002/keys/name
```

`PUT /keys/{key}` accepts `{ "value": "a string" }`. `GET /keys/{key}` returns the value or `404`. `/health` reports whether that node has initialized its Kommander partitions: it returns 200 when the node is ready, and 503 before that. `/alive` returns 200 while the process runs.

## Stop a node

A stop (SIGTERM, Ctrl+C, or Stop in the Aspire dashboard) disposes the Kommander node before Kestrel stops. The node does not call `LeaveCluster`. In Kommander 1.14, `LeaveCluster` removes the node from the committed voter roster, and a restart must not do that. The other nodes elect new leaders for its partitions in about one second.

For production, replace cleartext gRPC with TLS and node authentication, use externally reachable advertised addresses, and add snapshot/state-transfer handling before enabling elastic partition splits.

IDistributedApplicationBuilder builder = DistributedApplication.CreateBuilder(args);

// Raft identifies a node by its "host:port" gRPC endpoint, and a follower redirects a client to the
// REST URL of the leader. Both must be the real addresses of the processes, so the endpoints use fixed
// ports with no Aspire proxy. The ports are the same as in scripts/run-cluster.sh, so do not run both.
const string Host = "127.0.0.1";
const int NodeCount = 3;

int partitions = int.TryParse(builder.Configuration["SimpleDb:Partitions"], out int configuredPartitions) ? configuredPartitions : 8;

// Each node keeps its SQLite values and its Raft WAL under <data>/node<id>, so the data stays when you
// run the AppHost again. Remove the directory for a clean cluster.
string dataDirectory = builder.Configuration["SimpleDb:DataDirectory"] is { Length: > 0 } configured
    ? Path.GetFullPath(configured)
    : Path.Combine(Path.GetTempPath(), "simpledb-aspire");

var nodes = Enumerable.Range(1, NodeCount)
    .Select(id => (Id: id, HttpPort: 7000 + id, GrpcPort: 7100 + id))
    .ToArray();

string endpointMap = string.Join(',', nodes.Select(node => $"{Host}:{node.GrpcPort}=http://{Host}:{node.HttpPort}"));

foreach (var node in nodes)
{
    string peers = string.Join(',', nodes.Where(peer => peer.Id != node.Id).Select(peer => $"{Host}:{peer.GrpcPort}"));

    // No WaitFor between the nodes: a node is healthy only after a majority is up, so each node must
    // start without the others.
    builder.AddProject<Projects.SimpleDb>($"simpledb-{node.Id}")
        .WithHttpEndpoint(port: node.HttpPort, targetPort: node.HttpPort, name: "http", isProxied: false)
        .WithEndpoint(port: node.GrpcPort, targetPort: node.GrpcPort, name: "raft", isProxied: false)
        .WithEnvironment("NodeId", node.Id.ToString())
        .WithEnvironment("Host", Host)
        .WithEnvironment("HttpPort", node.HttpPort.ToString())
        .WithEnvironment("GrpcPort", node.GrpcPort.ToString())
        .WithEnvironment("Partitions", partitions.ToString())
        .WithEnvironment("DataDirectory", Path.Combine(dataDirectory, $"node{node.Id}"))
        .WithEnvironment("PeerGrpcEndpoints", peers)
        .WithEnvironment("EndpointMap", endpointMap)
        .WithHttpHealthCheck("/health", endpointName: "http")
        .WithUrlForEndpoint("http", url => url.DisplayText = "REST")
        .WithUrlForEndpoint("raft", url => url.DisplayLocation = UrlDisplayLocation.DetailsOnly);
}

builder.Build().Run();

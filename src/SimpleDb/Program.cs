using System.Net;
using System.Text.Json;
using Kommander;
using Kommander.Communication.Grpc;
using Kommander.Discovery;
using Kommander.Time;
using Kommander.WAL;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using SimpleDb;

WebApplicationBuilder builder = WebApplication.CreateBuilder(args);
builder.Logging.AddFilter("Microsoft.AspNetCore.Hosting.Diagnostics", LogLevel.Warning);
builder.Logging.AddFilter("Microsoft.AspNetCore.Routing.EndpointMiddleware", LogLevel.Warning);
builder.Logging.AddFilter("Microsoft.AspNetCore.Http.Result", LogLevel.Warning);
NodeOptions options = builder.Configuration.Get<NodeOptions>() ?? new NodeOptions();
options.Validate();

builder.WebHost.ConfigureKestrel(kestrel =>
{
    kestrel.Listen(IPAddress.Parse(options.Host), options.HttpPort, listen => listen.Protocols = HttpProtocols.Http1);
    kestrel.Listen(IPAddress.Parse(options.Host), options.GrpcPort, listen => listen.Protocols = HttpProtocols.Http2);
});

builder.Services.AddSingleton(options);
builder.Services.AddSingleton<KeyValueStore>();
builder.Services.AddSingleton<IRaft>(services =>
{
    ILogger<IRaft> logger = services.GetRequiredService<ILogger<IRaft>>();
    string walDirectory = Path.Combine(options.DataDirectory, "raft");
    Directory.CreateDirectory(walDirectory);

    RaftConfiguration configuration = new()
    {
        NodeName = $"simpledb-{options.NodeId}",
        NodeId = options.NodeId,
        Host = options.Host,
        Port = options.GrpcPort,
        InitialPartitions = options.Partitions,
        GrpcScheme = "http://",
        HttpScheme = "http://",
        TransportSecurity = new RaftTransportSecurityOptions { RequireTls = false },
        HeartbeatInterval = TimeSpan.FromMilliseconds(100),
        RecentHeartbeat = TimeSpan.FromMilliseconds(50),
        VotingTimeout = TimeSpan.FromMilliseconds(500),
        CheckLeaderInterval = TimeSpan.FromMilliseconds(50),
        UpdateNodesInterval = TimeSpan.FromMilliseconds(250),
        TimerInitialDelay = TimeSpan.FromMilliseconds(100),
        StartElectionTimeout = 300,
        EndElectionTimeout = 700,
        EnableQuiescence = false
    };

    return new RaftManager(
        configuration,
        new StaticDiscovery([.. options.Peers.Select(endpoint => new RaftNode(endpoint))]),
        new SqliteWAL(walDirectory, "v1", logger),
        new GrpcCommunication(),
        new HybridLogicalClock(),
        logger);
});
builder.Services.AddHostedService<ClusterService>();
builder.Services.AddKommanderGrpc();

WebApplication app = builder.Build();
app.MapGrpcRaftRoutes();

app.MapGet("/health", (IRaft raft) => Results.Ok(new { ready = raft.IsInitialized, node = options.NodeId }));

app.MapPut("/keys/{key}", async (string key, PutCommand body, IRaft raft, KeyValueStore store, CancellationToken cancellationToken) =>
{
    if (string.IsNullOrWhiteSpace(key) || body.Value is null)
        return Results.BadRequest(new { error = "A non-empty key and string value are required." });
        
    if (!raft.IsInitialized)
        return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);

    int partition = raft.GetPartitionKey(key);
    IResult? redirect = await RedirectIfFollower(key, partition, raft, options, cancellationToken);
    if (redirect is not null)
        return redirect;

    byte[] payload = JsonSerializer.SerializeToUtf8Bytes(new PutCommand(key, body.Value));
    RaftReplicationResult result = await raft.ReplicateLogs(partition, ClusterService.PutLogType, payload, cancellationToken: cancellationToken);
    if (!result.Success)
        return Results.Json(new { error = $"Replication failed: {result.Status}" }, statusCode: StatusCodes.Status503ServiceUnavailable);

    // Kommander invokes OnReplicationReceived on followers. The proposer applies
    // its own committed state-machine command after ReplicateLogs succeeds.
    store.Put(key, body.Value);
    return Results.Ok(new { key, value = body.Value, partition, logIndex = result.LogIndex });
});

app.MapGet("/keys/{key}", async (string key, IRaft raft, KeyValueStore store, CancellationToken cancellationToken) =>
{
    if (!raft.IsInitialized)
        return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);

    int partition = raft.GetPartitionKey(key);
    IResult? redirect = await RedirectIfFollower(key, partition, raft, options, cancellationToken);
    if (redirect is not null)
        return redirect;

    string? value = store.Get(key);
    return value is null ? Results.NotFound() : Results.Ok(new { key, value, partition });
});

app.Run();

static async Task<IResult?> RedirectIfFollower(
    string key,
    int partition,
    IRaft raft,
    NodeOptions options,
    CancellationToken cancellationToken)
{
    if (await raft.AmILeaderQuick(partition))
        return null;

    using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
    timeout.CancelAfter(TimeSpan.FromSeconds(3));
    try
    {
        string leader = await raft.WaitForLeader(partition, timeout.Token);
        if (!options.RestEndpoints.TryGetValue(leader, out string? restBase))
            return Results.Json(new { error = $"No REST endpoint is configured for leader {leader}." }, statusCode: 503);

        string location = $"{restBase.TrimEnd('/')}/keys/{Uri.EscapeDataString(key)}";
        return Results.Redirect(location, permanent: false, preserveMethod: true);
    }
    catch (OperationCanceledException)
    {
        return Results.Json(new { error = "No partition leader is currently available." }, statusCode: 503);
    }
}

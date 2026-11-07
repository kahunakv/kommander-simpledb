using System.Text.Json;
using Kommander;
using Kommander.Data;

namespace SimpleDb;

public sealed class ClusterService : BackgroundService, IHostedLifecycleService
{
    public const string PutLogType = "simpledb.put";

    private readonly IRaft raft;
    private readonly KeyValueStore store;
    private readonly ILogger<ClusterService> logger;

    public ClusterService(IRaft raft, KeyValueStore store, ILogger<ClusterService> logger)
    {
        this.raft = raft;
        this.store = store;
        this.logger = logger;
        raft.OnReplicationReceived += Apply;
        raft.OnLogRestored += Apply;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("Joining the Kommander cluster");
        await raft.JoinCluster(stoppingToken);
        logger.LogInformation("Node joined; {Partitions} application partitions are ready", raft.Configuration.InitialPartitions);
        await Task.Delay(Timeout.InfiniteTimeSpan, stoppingToken);
    }

    // Stop the node, but do not leave the cluster. In Kommander 1.14, LeaveCluster commits a
    // RemoveMember for this node, so a restart (Ctrl+C, or Stop/Restart in the Aspire dashboard) would
    // drop it from the voter roster, and the node keeps campaigning while the removal is pending.
    //
    // The host calls StoppingAsync before it stops any hosted service. That is important: Kestrel stops
    // first and waits for the open Raft gRPC streams of the peers, and during that wait a node that is
    // not yet disposed keeps its leaderships. Dispose stops the timers and the heartbeats at once, so
    // the peers elect new leaders in about a second. Dispose is idempotent, so the second call from
    // the container does nothing.
    public Task StoppingAsync(CancellationToken cancellationToken)
    {
        logger.LogInformation("Stopping the Kommander node");
        if (raft is IDisposable disposable)
            disposable.Dispose();
        return Task.CompletedTask;
    }

    public Task StartingAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StartedAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StoppedAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    private Task<bool> Apply(int _, RaftLog log)
    {
        if (log.LogType != PutLogType || log.LogData is null)
            return Task.FromResult(true);

        PutCommand? command = JsonSerializer.Deserialize<PutCommand>(log.LogData);
        if (command is null)
            return Task.FromResult(false);

        store.Put(command.Key, command.Value);
        return Task.FromResult(true);
    }
}

public sealed record PutCommand(string Key, string Value);

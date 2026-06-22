using System.Text.Json;
using Kommander;
using Kommander.Data;

namespace SimpleDb;

public sealed class ClusterService : BackgroundService
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

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        if (raft.Joined)
            await raft.LeaveCluster(cancellationToken: cancellationToken);
        await base.StopAsync(cancellationToken);
    }

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

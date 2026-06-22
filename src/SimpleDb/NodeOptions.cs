namespace SimpleDb;

public sealed class NodeOptions
{
    public int NodeId { get; init; }
    public string Host { get; init; } = "127.0.0.1";
    public int HttpPort { get; init; }
    public int GrpcPort { get; init; }
    public int Partitions { get; init; } = 8;
    public string DataDirectory { get; init; } = "data";
    public string PeerGrpcEndpoints { get; init; } = "";
    public string EndpointMap { get; init; } = "";

    public IReadOnlyList<string> Peers => Split(PeerGrpcEndpoints);

    public IReadOnlyDictionary<string, string> RestEndpoints => Split(EndpointMap)
        .Select(item => item.Split('=', 2, StringSplitOptions.TrimEntries))
        .Where(parts => parts.Length == 2)
        .ToDictionary(parts => parts[0], parts => parts[1], StringComparer.OrdinalIgnoreCase);

    public void Validate()
    {
        if (NodeId <= 0 || HttpPort <= 0 || GrpcPort <= 0)
            throw new InvalidOperationException("NodeId, HttpPort, and GrpcPort must be positive.");
        if (Peers.Count < 2)
            throw new InvalidOperationException("PeerGrpcEndpoints must contain the other two nodes.");
        if (RestEndpoints.Count != 3)
            throw new InvalidOperationException("EndpointMap must map all three gRPC endpoints to REST base URLs.");
    }

    private static string[] Split(string value) => value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}

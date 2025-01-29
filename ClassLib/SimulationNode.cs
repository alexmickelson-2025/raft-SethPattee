//SimulationNode.cs
public class SystemClock : IClock
{
    private DateTime _currentTime = DateTime.UtcNow;

    public DateTime GetCurrentTime() => _currentTime;

    public void AdvanceBy(TimeSpan duration)
    {
        _currentTime = _currentTime.Add(duration);
    }
}
public class MockTransport : ITransport
{
    private readonly Dictionary<string, RaftNode> _nodes = new();
    private int _networkDelay;

    public MockTransport()
    {
        _networkDelay = 0;
    }

    public void SetNetworkDelay(int milliseconds)
    {
        _networkDelay = milliseconds;
    }

    public void AddNode(RaftNode node)
    {
        _nodes[node.NodeId] = node;
    }

    public IEnumerable<string> GetOtherNodeIds(string currentNodeId)
    {
        return _nodes.Keys.Where(id => id != currentNodeId);
    }

    public async Task<AppendEntriesResponse> SendAppendEntriesAsync(AppendEntries entries, string recipientNodeId)
    {
        if (_networkDelay > 0)
        {
            await Task.Delay(_networkDelay);
        }

        if (!_nodes.TryGetValue(recipientNodeId, out var recipientNode))
        {
            throw new InvalidOperationException($"Node {recipientNodeId} not found");
        }

        await recipientNode.ReceiveAppendEntriesAsync(entries);
        return new AppendEntriesResponse
        {
            Success = recipientNode.LastAppendEntriesAccepted,
            Term = recipientNode.Term,
            LastLogIndex = recipientNode.GetLog().Count - 1
        };
    }

    public async Task<bool> SendVoteRequestAsync(VoteRequest request, string recipientNodeId)
    {
        if (_networkDelay > 0)
        {
            await Task.Delay(_networkDelay);
        }

        if (!_nodes.TryGetValue(recipientNodeId, out var recipientNode))
        {
            throw new InvalidOperationException($"Node {recipientNodeId} not found");
        }

        recipientNode.ReceiveVoteRequest(request);
        return recipientNode.LastVoteGranted;
    }

    public async Task SendAppendEntriesResponseAsync(AppendEntriesResponse response, string recipientNodeId)
    {
        if (_networkDelay > 0)
        {
            await Task.Delay(_networkDelay);
        }

        if (!_nodes.TryGetValue(recipientNodeId, out var recipientNode))
        {
            throw new InvalidOperationException($"Node {recipientNodeId} not found");
        }

        await recipientNode.ReceiveAppendEntriesResponseAsync(response, recipientNode.NodeId);
    }
}
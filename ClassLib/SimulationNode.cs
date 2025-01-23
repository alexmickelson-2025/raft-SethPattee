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

    public MockTransport(int initialDelay)
    {
        _networkDelay = initialDelay;
    }

    public void AddNode(RaftNode node)
    {
        _nodes[node.NodeId] = node;
    }

    public IEnumerable<string> GetOtherNodeIds(string currentNodeId)
    {
        return _nodes.Keys.Where(id => id != currentNodeId);
    }

    public async Task SendAppendEntriesAsync(AppendEntries entries, string recipientNodeId)
    {
        await Task.Delay(_networkDelay);
        if (_nodes.TryGetValue(recipientNodeId, out var recipientNode))
        {
            recipientNode.ReceiveAppendEntries(entries);
        }
    }

    public async Task<bool> SendVoteRequestAsync(VoteRequest request, string recipientNodeId)
    {
        await Task.Delay(_networkDelay);
        if (_nodes.TryGetValue(recipientNodeId, out var recipientNode))
        {
            recipientNode.ReceiveVoteRequest(request);
            return recipientNode.LastVoteGranted;
        }
        return false;
    }

    public async Task<AppendEntriesResponse> SendAppendEntriesResponseAsync(AppendEntriesResponse response, string recipientNodeId)
    {
        await Task.Delay(_networkDelay);
        if (_nodes.TryGetValue(recipientNodeId, out var recipientNode))
        {
            if (response.Success)
            {
                recipientNode.ResetElectionTimer();
            }
        }
        return response; 
    }

    public void SetNetworkDelay(int delay)
    {
        _networkDelay = delay;
    }

    Task ITransport.SendAppendEntriesResponseAsync(AppendEntriesResponse response, string recipientNodeId)
    {
        throw new NotImplementedException();
    }

    Task<AppendEntriesResponse> ITransport.SendAppendEntriesAsync(AppendEntries entries, string recipientNodeId)
    {
        throw new NotImplementedException();
    }
}

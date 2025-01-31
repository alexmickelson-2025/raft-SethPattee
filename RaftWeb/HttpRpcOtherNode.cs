using System.Net.Http.Json;

public class HttpRpcOtherNode
{
    public int Id { get; }
    public string Url { get; }
    private HttpClient client = new();

    public HttpRpcOtherNode(int id, string url)
    {
        Id = id;
        Url = url;
    }

    public async Task SendAppendEntriesAsync(AppendEntries entries)
    {
        try
        {
            await client.PostAsJsonAsync(Url + "/request/appendEntries", new AppendEntriesData
            {
                LeaderId = entries.LeaderId,
                Term = entries.Term,
                PrevLogIndex = entries.PrevLogIndex,
                PrevLogTerm = entries.PrevLogTerm,
                LeaderCommit = entries.LeaderCommit,
                LogEntries = entries.LogEntries
            });
        }
        catch (HttpRequestException)
        {
            Console.WriteLine($"Node {Url} is down");
        }
    }

    public async Task SendVoteRequestAsync(VoteRequest request)
    {
        try
        {
            await client.PostAsJsonAsync(Url + "/request/vote", new VoteRequestData
            {
                CandidateId = request.CandidateId,
                Term = request.Term,
                LastLogIndex = request.LastLogIndex,
                LastLogTerm = request.LastLogTerm
            });
        }
        catch (HttpRequestException)
        {
            Console.WriteLine($"Node {Url} is down");
        }
    }

    public async Task SendCommandAsync(string command)
    {
        try
        {
            await client.PostAsJsonAsync(Url + "/request/command", new ClientCommandData
            {
                Command = command
            });
        }
        catch (HttpRequestException)
        {
            Console.WriteLine($"Node {Url} is down");
        }
    }
}

public class VoteResponseData
{
    public bool VoteGranted { get; set; }
    public int Term { get; set; }
}

public class RespondEntriesData
{
    public bool Success { get; set; }
    public int Term { get; set; }
    public int LastLogIndex { get; set; }
}

public class AppendEntriesData
{
    public string LeaderId { get; set; }
    public int Term { get; set; }
    public int PrevLogIndex { get; set; }
    public int PrevLogTerm { get; set; }
    public int LeaderCommit { get; set; }
    public List<LogEntry> LogEntries { get; set; } = new List<LogEntry>();
}

public class VoteRequestData
{
    public string CandidateId { get; set; }
    public int Term { get; set; }
    public int LastLogIndex { get; set; }
    public int LastLogTerm { get; set; }
}

public class ClientCommandData
{
    public string Command { get; set; }
}

public class NodeData
{
    public string NodeId { get; set; }
    public NodeState State { get; set; }
    public int Term { get; set; }
    public string? CurrentLeaderId { get; set; }
    public int CommitIndex { get; set; }
    public List<LogEntry> Log { get; set; }
    public bool Paused { get; set; }
}

public class CommandResult
{
    public bool Success { get; set; }
    public string Message { get; set; }
    public Dictionary<string, string> StateMachineState { get; set; }
}
public class HttpTransport : ITransport
{
    private readonly string _currentNodeId;
    private readonly List<HttpRpcOtherNode> _otherNodes;
    private readonly MockTransport _mockTransport;

    public HttpTransport(string currentNodeId, string otherNodesRaw)
    {
        _currentNodeId = currentNodeId;
        _mockTransport = new MockTransport();

        _otherNodes = otherNodesRaw
            .Split(";")
            .Where(s => !string.IsNullOrEmpty(s))
            .Select(s =>
            {
                var parts = s.Split(",");
                return new HttpRpcOtherNode(int.Parse(parts[0]), parts[1]);
            })
            .ToList();
    }

    public IEnumerable<string> GetOtherNodeIds(string currentNodeId)
    {
        return _otherNodes.Select(n => n.Id.ToString());
    }

    public async Task<AppendEntriesResponse> SendAppendEntriesAsync(AppendEntries entries, string recipientNodeId)
    {
        var node = _otherNodes.FirstOrDefault(n => n.Id.ToString() == recipientNodeId);
        if (node == null) throw new InvalidOperationException($"Node {recipientNodeId} not found");

        try
        {
            await node.SendAppendEntriesAsync(entries);
            return new AppendEntriesResponse
            {
                Success = true,
                Term = entries.Term,
                LastLogIndex = entries.LogEntries.Count - 1
            };
        }
        catch
        {
            return new AppendEntriesResponse
            {
                Success = false,
                Term = entries.Term,
                LastLogIndex = -1
            };
        }
    }

    public async Task<bool> SendVoteRequestAsync(VoteRequest request, string recipientNodeId)
    {
        var node = _otherNodes.FirstOrDefault(n => n.Id.ToString() == recipientNodeId);
        if (node == null) throw new InvalidOperationException($"Node {recipientNodeId} not found");

        try
        {
            await node.SendVoteRequestAsync(request);
            return true;
        }
        catch
        {
            return false;
        }
    }

    public async Task SendAppendEntriesResponseAsync(AppendEntriesResponse response, string recipientNodeId)
    {
        //add endpoint??
        Console.WriteLine($"Sending AppendEntries response to {recipientNodeId}");
    }
}
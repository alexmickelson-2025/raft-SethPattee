//HttpRpcOtherNode.cs
public class HttpTransport : ITransport
{
    private readonly string _currentNodeId;
    private readonly List<HttpRpcOtherNode> _otherNodes;
    private readonly ILogger<HttpTransport> _logger;

    public HttpTransport(string currentNodeId, string otherNodesRaw, ILogger<HttpTransport> logger)
    {
        _currentNodeId = currentNodeId;
        _logger = logger;

        // Parse other nodes from environment variable
        // Format: "1,http://node1:8080;2,http://node2:8080;3,http://node3:8080"
        _otherNodes = otherNodesRaw
            .Split(';')
            .Where(s => !string.IsNullOrEmpty(s))
            .Select(s =>
            {
                var parts = s.Split(',');
                return new HttpRpcOtherNode(
                    int.Parse(parts[0]), 
                    parts[1].TrimEnd('/'),
                    logger
                );
            })
            .ToList();

        _logger.LogInformation("Initialized HttpTransport for node {NodeId} with {Count} other nodes", 
            currentNodeId, _otherNodes.Count);
    }

    public IEnumerable<string> GetOtherNodeIds(string currentNodeId)
    {
        return _otherNodes.Select(n => n.Id.ToString());
    }

    public async Task<AppendEntriesResponse> SendAppendEntriesAsync(AppendEntries entries, string recipientNodeId)
    {
        var node = _otherNodes.FirstOrDefault(n => n.Id.ToString() == recipientNodeId);
        if (node == null)
        {
            _logger.LogError("Node {NodeId} not found", recipientNodeId);
            throw new InvalidOperationException($"Node {recipientNodeId} not found");
        }

        try
        {
            var response = await node.SendAppendEntriesAsync(entries);
            _logger.LogDebug("Sent AppendEntries to {NodeId}, Success: {Success}", 
                recipientNodeId, response?.Success);
            return response ?? new AppendEntriesResponse 
            { 
                Success = false, 
                Term = entries.Term 
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to send AppendEntries to {NodeId}", recipientNodeId);
            return new AppendEntriesResponse
            {
                Success = false,
                Term = entries.Term
            };
        }
    }

    public async Task<bool> SendVoteRequestAsync(VoteRequest request, string recipientNodeId)
    {
        var node = _otherNodes.FirstOrDefault(n => n.Id.ToString() == recipientNodeId);
        if (node == null)
        {
            _logger.LogError("Node {NodeId} not found", recipientNodeId);
            throw new InvalidOperationException($"Node {recipientNodeId} not found");
        }

        try
        {
            var response = await node.SendVoteRequestAsync(request);
            _logger.LogDebug("Sent VoteRequest to {NodeId}, Granted: {Granted}", 
                recipientNodeId, response?.VoteGranted);
            return response?.VoteGranted ?? false;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to send VoteRequest to {NodeId}", recipientNodeId);
            return false;
        }
    }

    public async Task SendAppendEntriesResponseAsync(AppendEntriesResponse response, string recipientNodeId)
    {
        if (string.IsNullOrEmpty(recipientNodeId))
        {
            _logger.LogWarning("No recipient specified for AppendEntriesResponse");
            return;
        }

        var node = _otherNodes.FirstOrDefault(n => n.Id.ToString() == recipientNodeId);
        if (node == null)
        {
            _logger.LogError("Node {NodeId} not found", recipientNodeId);
            return;
        }

        try
        {
            await node.SendAppendEntriesResponseAsync(response);
            _logger.LogDebug("Sent AppendEntriesResponse to {NodeId}", recipientNodeId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to send AppendEntriesResponse to {NodeId}", recipientNodeId);
        }
    }
}

// Updated HttpRpcOtherNode class with better error handling and logging
public class HttpRpcOtherNode
{
    public int Id { get; }
    private readonly string _url;
    private readonly HttpClient _client;
    private readonly ILogger _logger;

    public HttpRpcOtherNode(int id, string url, ILogger logger)
    {
        Id = id;
        _url = url;
        _logger = logger;
        _client = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(5)
        };
    }

    public async Task<AppendEntriesResponse> SendAppendEntriesAsync(AppendEntries entries)
    {
        try
        {
            var response = await _client.PostAsJsonAsync(
                $"{_url}/request/appendEntries",
                new AppendEntriesData
                {
                    LeaderId = entries.LeaderId,
                    Term = entries.Term,
                    PrevLogIndex = entries.PrevLogIndex,
                    PrevLogTerm = entries.PrevLogTerm,
                    LeaderCommit = entries.LeaderCommit,
                    LogEntries = entries.LogEntries
                });

            if (response.IsSuccessStatusCode)
            {
                return await response.Content.ReadFromJsonAsync<AppendEntriesResponse>() 
                    ?? new AppendEntriesResponse { Success = false, Term = entries.Term };
            }
            
            _logger.LogWarning("Failed AppendEntries request to {Url}: {StatusCode}", 
                _url, response.StatusCode);
            return new AppendEntriesResponse { Success = false, Term = entries.Term };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error sending AppendEntries to {Url}", _url);
            return new AppendEntriesResponse { Success = false, Term = entries.Term };
        }
    }

    public async Task<VoteResponseData> SendVoteRequestAsync(VoteRequest request)
    {
        try
        {
            var response = await _client.PostAsJsonAsync(
                $"{_url}/request/vote",
                new VoteRequestData
                {
                    CandidateId = request.CandidateId,
                    Term = request.Term,
                    LastLogIndex = request.LastLogIndex,
                    LastLogTerm = request.LastLogTerm
                });

            if (response.IsSuccessStatusCode)
            {
                return await response.Content.ReadFromJsonAsync<VoteResponseData>()
                    ?? new VoteResponseData { VoteGranted = false, Term = request.Term };
            }
            
            _logger.LogWarning("Failed VoteRequest to {Url}: {StatusCode}", 
                _url, response.StatusCode);
            return new VoteResponseData { VoteGranted = false, Term = request.Term };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error sending VoteRequest to {Url}", _url);
            return new VoteResponseData { VoteGranted = false, Term = request.Term };
        }
    }

    public async Task SendAppendEntriesResponseAsync(AppendEntriesResponse response)
    {
        try
        {
            var result = await _client.PostAsJsonAsync($"{_url}/response/appendEntries", response);
            if (!result.IsSuccessStatusCode)
            {
                _logger.LogWarning("Failed to send AppendEntriesResponse to {Url}: {StatusCode}", 
                    _url, result.StatusCode);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error sending AppendEntriesResponse to {Url}", _url);
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

public class CommandResult
{
    public bool Success { get; set; }
    public string Message { get; set; }
    public Dictionary<string, string> StateMachineState { get; set; }
}
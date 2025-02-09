using System.Net.Http.Json;

public interface IHttpTransport : ITransport
{
    string BaseUrl { get; }
    int NodeId { get; }
}

public class HttpRpcTransport : IHttpTransport
{
    private readonly Dictionary<string, string> _nodeUrls;
    private readonly HttpClient _client;
    public string BaseUrl { get; }
    public int NodeId { get; }

    public HttpRpcTransport(int nodeId, string baseUrl, Dictionary<string, string> nodeUrls)
    {
        NodeId = nodeId;
        BaseUrl = baseUrl;
        _nodeUrls = nodeUrls;
        _client = new HttpClient();
    }

    public IEnumerable<string> GetOtherNodeIds(string currentNodeId)
    {
        return _nodeUrls.Keys.Where(id => id != currentNodeId);
    }

    public async Task<AppendEntriesResponse> SendAppendEntriesAsync(AppendEntries entries, string recipientNodeId)
    {
        try
        {
            if (!_nodeUrls.TryGetValue(recipientNodeId, out var url))
            {
                throw new InvalidOperationException($"URL not found for node {recipientNodeId}");
            }

            var request = new AppendEntriesRequest
            {
                Term = entries.Term,
                LeaderId = entries.LeaderId,
                PrevLogIndex = entries.PrevLogIndex,
                PrevLogTerm = entries.PrevLogTerm,
                LeaderCommit = entries.LeaderCommit,
                Entries = entries.LogEntries
            };

            var response = await _client.PostAsJsonAsync($"{url}/request/appendEntries", request);
            response.EnsureSuccessStatusCode();
            
            var result = await response.Content.ReadFromJsonAsync<AppendEntriesResponse>();
            return result ?? new AppendEntriesResponse { Success = false, Term = entries.Term };
        }
        catch (HttpRequestException ex)
        {
            Console.WriteLine($"Node {recipientNodeId} is unreachable: {ex.Message}");
            return new AppendEntriesResponse { Success = false, Term = entries.Term };
        }
    }

    public async Task<bool> SendVoteRequestAsync(VoteRequest request, string recipientNodeId)
    {
        try
        {
            if (!_nodeUrls.TryGetValue(recipientNodeId, out var url))
            {
                throw new InvalidOperationException($"URL not found for node {recipientNodeId}");
            }

            var response = await _client.PostAsJsonAsync($"{url}/request/vote", request);
            response.EnsureSuccessStatusCode();
            
            var result = await response.Content.ReadFromJsonAsync<VoteResponse>();
            return result?.VoteGranted ?? false;
        }
        catch (HttpRequestException ex)
        {
            Console.WriteLine($"Node {recipientNodeId} is unreachable: {ex.Message}");
            return false;
        }
    }

    public async Task SendAppendEntriesResponseAsync(AppendEntriesResponse response, string recipientNodeId)
    {
        try
        {
            if (!_nodeUrls.TryGetValue(recipientNodeId, out var url))
            {
                throw new InvalidOperationException($"URL not found for node {recipientNodeId}");
            }

            var httpResponse = await _client.PostAsJsonAsync($"{url}/response/appendEntries", response);
            httpResponse.EnsureSuccessStatusCode();
        }
        catch (HttpRequestException ex)
        {
            Console.WriteLine($"Node {recipientNodeId} is unreachable: {ex.Message}");
        }
    }
}

// Data Transfer Objects
public class AppendEntriesRequest
{
    public int Term { get; set; }
    public string LeaderId { get; set; } = string.Empty;
    public int PrevLogIndex { get; set; }
    public int PrevLogTerm { get; set; }
    public int LeaderCommit { get; set; }
    public List<LogEntry> Entries { get; set; } = new();
}

public class VoteResponse
{
    public bool VoteGranted { get; set; }
    public int Term { get; set; }
}

public class ClientCommandRequest
{
    public string Command { get; set; } = string.Empty;
}

public class ClientCommandResponse
{
    public bool Success { get; set; }
    public string Message { get; set; } = string.Empty;
}
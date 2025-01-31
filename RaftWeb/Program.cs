using System.Text.Json;
using OpenTelemetry.Logs;
using OpenTelemetry.Resources;

var builder = WebApplication.CreateBuilder(args);
builder.WebHost.UseUrls("http://0.0.0.0:8080");

var nodeId = Environment.GetEnvironmentVariable("NODE_ID") ?? throw new Exception("NODE_ID environment variable not set");
var otherNodesRaw = Environment.GetEnvironmentVariable("OTHER_NODES") ?? throw new Exception("OTHER_NODES environment variable not set");
var nodeIntervalScalarRaw = Environment.GetEnvironmentVariable("NODE_INTERVAL_SCALAR") ?? throw new Exception("NODE_INTERVAL_SCALAR environment variable not set");

builder.Services.AddLogging();
var serviceName = "Node" + nodeId;
builder.Logging.AddOpenTelemetry(options =>
{
    options
        .SetResourceBuilder(
            ResourceBuilder
                .CreateDefault()
                .AddService(serviceName)
        )
        .AddOtlpExporter(options =>
        {
            options.Endpoint = new Uri("http://dashboard:18889");
        });
});

var transport = new HttpRpcTransport(nodeId, otherNodesRaw);
var clock = new SystemClock();
var stateMachine = new SimpleStateMachine();
NodeState initialState = NodeState.Follower;

var node = new RaftNode(nodeId, initialState, clock,  transport, stateMachine);

var app = builder.Build();

app.MapGet("/health", () => "healthy");

// Node data endpoint
// app.MapGet("/nodeData", () => new NodeData
// {
//     NodeId = node.NodeId,
//     State = node.State,
//     Term = node.Term,
//     CurrentLeaderId = node.CurrentLeaderId,
//     CommitIndex = node.CommitIndex,
//     Log = node.GetLog(),
//     Paused = node.Paused
// });

// Append entries request endpoint
app.MapPost("/request/appendEntries", async (AppendEntriesData request) =>
{
    var appendEntries = new AppendEntries
    {
        LeaderId = request.LeaderId,
        Term = request.Term,
        PrevLogIndex = request.PrevLogIndex,
        PrevLogTerm = request.PrevLogTerm,
        LeaderCommit = request.LeaderCommit,
        LogEntries = request.LogEntries
    };

    await node.ReceiveAppendEntriesAsync(appendEntries);
    Console.WriteLine("request appendEntries");
    return new AppendEntriesResponse
    {
        Success = node.LastAppendEntriesAccepted,
        Term = node.Term,
        LastLogIndex = node.GetLog().Count - 1
    };
});

app.MapPost("/request/vote", async (VoteRequestData request) =>
{
    Console.WriteLine("request vote");
    var voteRequest = new VoteRequest
    {
        CandidateId = request.CandidateId,
        Term = request.Term,
        LastLogIndex = request.LastLogIndex,
        LastLogTerm = request.LastLogTerm
    };

    node.ReceiveVoteRequest(voteRequest);
    
    return new VoteResponseData 
    { 
        VoteGranted = node.LastVoteGranted, 
        Term = node.Term 
    };
});

app.MapPost("/request/command", async (ClientCommandData data) =>
{
    var result = await node.ReceiveClientCommandAsync(data.Command);
    Console.WriteLine("command");
    return new CommandResult
    {
        Success = result,
        Message = result ? "Command processed successfully" : "Command failed",
        StateMachineState = node.GetStateMachineState()
    };
});

_ = Task.Run(async () =>
{
    while (true)
    {
        await node.CheckElectionTimeoutAsync();
        await Task.Delay(100);
    }
});

_ = Task.Run(async () =>
{
    while (true)
    {
        if (node.State == NodeState.Leader)
        {
            await node.RunLeaderTasksAsync();
        }
        await Task.Delay(100);
    }
});

app.Run();

public class HttpRpcTransport : ITransport
{
    private readonly string _currentNodeId;
    private readonly List<HttpRpcOtherNode> _otherNodes;
    private readonly HttpClient _client = new();

    public HttpRpcTransport(string currentNodeId, string otherNodesRaw)
    {
        _currentNodeId = currentNodeId;
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
            var response = await _client.PostAsJsonAsync($"{node.Url}/request/appendEntries", new AppendEntriesData
            {
                LeaderId = entries.LeaderId,
                Term = entries.Term,
                PrevLogIndex = entries.PrevLogIndex,
                PrevLogTerm = entries.PrevLogTerm,
                LeaderCommit = entries.LeaderCommit,
                LogEntries = entries.LogEntries
            });

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
            var response = await _client.PostAsJsonAsync($"{node.Url}/request/vote", new VoteRequestData
            {
                CandidateId = request.CandidateId,
                Term = request.Term,
                LastLogIndex = request.LastLogIndex,
                LastLogTerm = request.LastLogTerm
            });

            return true;
        }
        catch
        {
            return false;
        }
    }

    public async Task SendAppendEntriesResponseAsync(AppendEntriesResponse response, string recipientNodeId)
    {
        var node = _otherNodes.FirstOrDefault(n => n.Id.ToString() == recipientNodeId);
        if (node == null) throw new InvalidOperationException($"Node {recipientNodeId} not found");

        try
        {
            await _client.PostAsJsonAsync($"{node.Url}/response/appendEntries", new RespondEntriesData
            {
                Success = response.Success,
                Term = response.Term,
                LastLogIndex = response.LastLogIndex
            });
        }
        catch
        {
            
        }
    }
}
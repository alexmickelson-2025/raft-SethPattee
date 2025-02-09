using System.Text.Json;
using OpenTelemetry.Logs;
using OpenTelemetry.Resources;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

var builder = WebApplication.CreateBuilder(args);
builder.WebHost.UseUrls("http://0.0.0.0:8080");

// Environment variables
var nodeId = Environment.GetEnvironmentVariable("NODE_ID") ?? 
    throw new Exception("NODE_ID environment variable not set");
var otherNodesRaw = Environment.GetEnvironmentVariable("OTHER_NODES") ?? 
    throw new Exception("OTHER_NODES environment variable not set");
var nodeIntervalScalarRaw = Environment.GetEnvironmentVariable("NODE_INTERVAL_SCALAR") ?? 
    throw new Exception("NODE_INTERVAL_SCALAR environment variable not set");

// Configure logging
builder.Services.AddLogging();
var serviceName = "Node" + nodeId;
builder.Logging.AddOpenTelemetry(options =>
{
    options.SetResourceBuilder(
        ResourceBuilder.CreateDefault()
            .AddService(serviceName)
    )
    .AddOtlpExporter(options =>
    {
        options.Endpoint = new Uri("http://dashboard:18889");
    });
});

// Build app
var app = builder.Build();
var logger = app.Services.GetService<ILogger<Program>>();
logger.LogInformation("Node ID {name}", nodeId);
logger.LogInformation("Other nodes environment config: {config}", otherNodesRaw);

// Parse other nodes configuration
var nodeUrls = otherNodesRaw
    .Split(";")
    .ToDictionary(
        s => s.Split(",")[0],
        s => s.Split(",")[1]
    );

logger.LogInformation("Other nodes configuration: {nodes}", JsonSerializer.Serialize(nodeUrls));

// Initialize transport and node
var transport = new HttpRpcTransport(
    nodeId: int.Parse(nodeId),
    baseUrl: $"http://0.0.0.0:8080",
    nodeUrls: nodeUrls
);

var raftNode = new RaftNode(
    nodeId: nodeId,
    initialState: NodeState.Follower,
    clock: new SystemClock(),
    transport: transport,
    stateMachine: new SimpleStateMachine()
);

// Configure election timeouts
raftNode.TimerLowerBound = (int)(1500 * double.Parse(nodeIntervalScalarRaw));
raftNode.TimerUpperBound = (int)(3000 * double.Parse(nodeIntervalScalarRaw));

// Start election monitoring
_ = Task.Run(async () =>
{
    while (true)
    {
        await raftNode.CheckElectionTimeoutAsync();
    }
});

// API Endpoints
app.MapGet("/health", () => "healthy");

app.MapGet("/nodeData", () => new
{
    Id = raftNode.NodeId,
    State = raftNode.State,
    Term = raftNode.Term,
    CurrentLeader = raftNode.CurrentLeaderId,
    CommitIndex = raftNode.CommitIndex,
    Log = raftNode.Log,
    StateMachine = raftNode.GetStateMachineState(),
    ElectionTimeout = raftNode.ElectionTimeout
});

app.MapPost("/request/appendEntries", async (AppendEntries request) =>
{
    logger.LogInformation("Received append entries request {request}", JsonSerializer.Serialize(request));
    
    // Process the request and produce a response.
    // (Adjust the logic here as needed to reflect your Raft implementation.)
    await raftNode.ReceiveAppendEntriesAsync(request);
    
    var appendEntriesResponse = new AppendEntriesResponse
    {
        Success = true,          // or false, based on your logic
        Term = raftNode.Term,    // current term of this node
        LastLogIndex = -1        // set this appropriately (e.g., the index of the last log entry)
    };
    
    return Results.Ok(appendEntriesResponse);
});

app.MapPost("/request/vote", async (VoteRequest request) =>
{
    logger.LogInformation("Received vote request {request}", JsonSerializer.Serialize(request));
    await raftNode.ReceiveVoteRequestAsync(request);
    return Results.Ok(new VoteResponse 
    { 
        VoteGranted = raftNode.LastVoteGranted,
        Term = raftNode.Term
    });
});

app.MapPost("/response/appendEntries", async (AppendEntriesResponse response) =>
{
    logger.LogInformation("Received append entries response {response}", JsonSerializer.Serialize(response));
    await raftNode.ReceiveAppendEntriesResponseAsync(response, response.Term.ToString());
    return Results.Ok();
});

app.MapPost("/request/command", async (ClientCommandRequest request) =>
{
    var result = await raftNode.ReceiveClientCommandAsync(request.Command);
    return Results.Ok(new ClientCommandResponse
    {
        Success = result,
        Message = result ? "Command processed successfully" : "Command processing failed"
    });
});


_ = Task.Run(async () =>
{
    while (true)
    {
        if (raftNode.State == NodeState.Leader)
        {
            await raftNode.RunLeaderTasksAsync();
        }
        if (raftNode.State == NodeState.Candidate)
        {
            await raftNode.StartElection();
        }
        await raftNode.CheckElectionTimeNodeIdoutAsync();
        await Task.Delay(50); // check frequently
    }
});


if (raftNode.State == NodeState.Leader)
{
    _ = Task.Run(async () =>
    {
        await raftNode.RunLeaderTasksAsync();
    });
}

await app.RunAsync();
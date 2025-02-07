using System.Text.Json;
using OpenTelemetry.Logs;
using OpenTelemetry.Resources;

//NodeWeb.cs

var builder = WebApplication.CreateBuilder(args);
builder.WebHost.UseUrls("http://0.0.0.0:8080");


// Environment variables
var nodeId = Environment.GetEnvironmentVariable("NODE_ID") ?? throw new Exception("NODE_ID environment variable not set");
var otherNodesRaw = Environment.GetEnvironmentVariable("OTHER_NODES") ?? throw new Exception("OTHER_NODES environment variable not set");
var nodeIntervalScalarRaw = Environment.GetEnvironmentVariable("NODE_INTERVAL_SCALAR") ?? throw new Exception("NODE_INTERVAL_SCALAR environment variable not set");

// Configure logging
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

var app = builder.Build();

var logger = app.Services.GetService<ILogger<Program>>();
logger.LogInformation("Node ID {name}", nodeId);
logger.LogInformation("Other nodes environment config: {config}", otherNodesRaw);

// Initialize system clock and transport
var clock = new SystemClock();
var transportLogger = app.Services.GetService<ILogger<HttpTransport>>();
var transport = new HttpTransport(nodeId, otherNodesRaw, transportLogger);
var stateMachine = new SimpleStateMachine();

// Create the Raft node
var node = new RaftNode(nodeId, NodeState.Leader, clock, transport, stateMachine);

// Set node interval scalar if provided
if (double.TryParse(nodeIntervalScalarRaw, out double scalar))
{
    if (scalar > 0)
    {
        node.TimerLowerBound = (int)(1500 * scalar);
        node.TimerUpperBound = (int)(3000 * scalar);
    }
}

app.MapGet("/health", () => "healthy");
app.MapPost("/response/appendEntries", async (AppendEntriesResponse response) =>
{
    logger.LogInformation("Received append entries response: {response}", response);
    await node.ReceiveAppendEntriesResponseAsync(response, nodeId);
    return Results.Ok();
});

app.MapGet("/nodeData", () => new NodeData
{
    NodeId = node.NodeId,
    State = node.State,
    Term = node.Term,
    CurrentLeaderId = node.CurrentLeaderId,
    CommitIndex = node.CommitIndex,
    Log = node.Log.ToList(),
    Paused = node.Paused
});

app.MapPost("/request/appendEntries", async (AppendEntriesData request) =>
{
    logger.LogInformation("received append entries request {request}", request);
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

    // Create and return a proper JSON response
    var responseData = new RespondEntriesData
    {
        Success = true,
        Term = node.Term,           // You might update this based on your Raft logic
        LastLogIndex = node.CommitIndex
    };

    return Results.Ok(responseData);
});


app.MapPost("/request/vote", async (VoteRequestData request) =>
{
    logger.LogInformation("received vote request {request}", request);
    var voteRequest = new VoteRequest
    {
        CandidateId = request.CandidateId,
        Term = request.Term,
        LastLogIndex = request.LastLogIndex,
        LastLogTerm = request.LastLogTerm
    };
    node.ReceiveVoteRequest(voteRequest);
    return Results.Ok(new VoteResponseData
    {
        VoteGranted = node.LastVoteGranted,
        Term = node.Term
    });
});


app.MapPost("/request/command", async (ClientCommandData data) =>
{
    logger.LogInformation("Received command: {Command}", data.Command);

    var success = await node.ReceiveClientCommandAsync(data.Command);
    
    if (success)
    {
        logger.LogInformation("Command processed successfully");
    }
    else
    {
        logger.LogWarning("Command processing failed");
    }

    return Results.Ok(new CommandResult
    {
        Success = success,
        Message = success ? "Command processed successfully" : "Command processing failed",
        StateMachineState = node.GetStateMachineState()
    });
});


_ = Task.Run(async () =>
{
    while (true)
    {
        if (node.State == NodeState.Leader)
        {
            await node.RunLeaderTasksAsync();
        }
        await node.CheckElectionTimeoutAsync();
        await Task.Delay(50); // check frequently
    }
});


if (node.State == NodeState.Leader)
{
    _ = Task.Run(async () =>
    {
        await node.RunLeaderTasksAsync();
    });
}

await app.RunAsync();
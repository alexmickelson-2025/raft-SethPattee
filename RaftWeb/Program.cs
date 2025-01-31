using System.Text.Json;
using OpenTelemetry.Logs;
using OpenTelemetry.Resources;
//using RaftLogic;

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
var app = builder.Build();

var logger = app.Services.GetService<ILogger<Program>>();
logger.LogInformation("Node ID {name}", nodeId);
logger.LogInformation("Other nodes environment config: {}", otherNodesRaw);


//INode[] otherNodes = otherNodesRaw
//  .Split(";")
//  .Select(s => new HttpRpcOtherNode(int.Parse(s.Split(",")[0]), s.Split(",")[1]))
//  .ToArray();


//logger.LogInformation("other nodes {nodes}", JsonSerializer.Serialize(otherNodes));


//var node = new RaftNode(otherNodes)
//{
//    Id = int.Parse(nodeId),
//    logger = app.Services.GetService<ILogger<RaftNode>>()
//};

//RaftNode.NodeIntervalScalar = double.Parse(nodeIntervalScalarRaw);

//node.RunElectionLoop();

//app.MapGet("/health", () => "healthy");

//app.MapGet("/nodeData", () =>
//{
//    return new NodeData(
//      Id: node.Id,
//      Status: node.Status,
//      ElectionTimeout: node.ElectionTimeout,
//      Term: node.CurrentTerm,
//      CurrentTermLeader: node.CurrentTermLeader,
//      CommittedEntryIndex: node.CommittedEntryIndex,
//      Log: node.Log,
//      State: node.State,
//      NodeIntervalScalar: RaftNode.NodeIntervalScalar
//    );
//});

//app.MapPost("/request/appendEntries", async (AppendEntriesData request) =>
//{
//    logger.LogInformation("received append entries request {request}", request);
//    await node.RequestAppendEntries(request);
//});

//app.MapPost("/request/vote", async (VoteRequestData request) =>
//{
//    logger.LogInformation("received vote request {request}", request);
//    await node.RequestVote(request);
//});

//app.MapPost("/response/appendEntries", async (RespondEntriesData response) =>
//{
//    logger.LogInformation("received append entries response {response}", response);
//    await node.RespondAppendEntries(response);
//});

//app.MapPost("/response/vote", async (VoteResponseData response) =>
//{
//    logger.LogInformation("received vote response {response}", response);
//    await node.ResponseVote(response);
//});

//app.MapPost("/request/command", async (ClientCommandData data) =>
//{
//    await node.SendCommand(data);
//});

app.Run();

using Xunit;
using NSubstitute;
using System.Collections.Generic;
using System.Threading.Tasks;

public class RaftNodeLogTests
{
    //#1 
[Fact]
public async Task Leader_ReceivesClientCommand_SendsEntryInAppendEntries()
{
    // Arrange
    var clock = new SystemClock();
    var transport = new MockTransport();
    
    // Create leader node
    var leader = new RaftNode("A", NodeState.Leader, clock, transport, new SimpleStateMachine());
    transport.AddNode(leader);
    
    // Create followers
    var followerB = new RaftNode("B", NodeState.Follower, clock, transport, new SimpleStateMachine());
    transport.AddNode(followerB);
    var followerC = new RaftNode("C", NodeState.Follower, clock, transport, new SimpleStateMachine());
    transport.AddNode(followerC);

    // Initialize leader's NextIndex for followers to match empty logs
    foreach (var nodeId in transport.GetOtherNodeIds(leader.NodeId))
    {
        leader.NextIndex[nodeId] = 0; // First entry starts at index 0
    }

    // Act: Send a client command to the leader
    string command = "SET key1 value1";
    bool commandResult = await leader.ReceiveClientCommandAsync(command);

    // Assert: Command was accepted by leader
    Assert.True(commandResult, "Command replication failed");

    // Force the leader to send heartbeats to ALL followers (not just majority)
    await leader.SendHeartbeatAsync();

    // Verify ALL followers received the entry
    Assert.Equal(command, leader.Log[0].Command);
    Assert.Equal(command, followerB.Log[0].Command);
    Assert.Equal(command, followerC.Log[0].Command);
}

    //#2
    [Fact]
    public async Task Leader_ReceivesClientCommand_AppendsToLog()
    {
        /// Arrange
        var clock = Substitute.For<IClock>();
        var transport = Substitute.For<ITransport>();
        var state = Substitute.For<IStateMachine>();
        var node = new RaftNode("leaderNode", NodeState.Leader, clock, transport, state);

        var newEntryIndex = 1;
        var otherNodes = new List<string> { "node1", "node2", "node3" };

        // Act
        bool commitSuccessful = await node.TryReplicateEntry(newEntryIndex, new Dictionary<string, bool>(), otherNodes, (otherNodes.Count + 1) / 2 + 1);

        // Assert
        Assert.False(commitSuccessful);
        
    }
    //#3
    [Fact]
    public void NewNode_HasEmptyLog()
    {
        // Arrange
        var clock = Substitute.For<IClock>();
        var transport = Substitute.For<ITransport>();
        var node = new RaftNode("node1", clock, transport);

        // Act
        var log = node.GetLog();

        // Assert
        Assert.Empty(log); 
    }
    //#4

[Fact]
public async Task Leader_WinsElection_InitializesNextIndexForFollowers()
{
    // Arrange
    var clock = Substitute.For<IClock>();
    var transport = Substitute.For<ITransport>();
    var state = Substitute.For<IStateMachine>();
    var node = new RaftNode("leaderNode", NodeState.Follower, clock, transport, state); 

    var otherNodeIds = new List<string> { "node1", "node2" };
    transport.GetOtherNodeIds("leaderNode").Returns(otherNodeIds);

    transport.SendVoteRequestAsync(Arg.Any<VoteRequest>(), Arg.Any<string>()).Returns(true);

    // Act
    await node.StartElection();
    await node.SendHeartbeatAsync();

    // Assert
    Assert.Equal(NodeState.Leader, node.State); 

    foreach (var id in otherNodeIds)
    {
        Assert.Equal(node.GetLog().Count, node.NextIndex[id]); 
    }
}
    //#5
[Fact]
    public async Task Leader_Initializes_NextIndex_For_Each_Follower()
    {
        // Arrange
        var nodeId = "leaderNode";
        var clock = Substitute.For<IClock>();
        var transport = Substitute.For<ITransport>();
        var stateMachine = Substitute.For<IStateMachine>();

        var followerIds = new List<string> { "follower1", "follower2", "follower3" };
        transport.GetOtherNodeIds(nodeId).Returns(followerIds);

        var raftNode = new RaftNode(nodeId, NodeState.Leader, clock, transport, stateMachine);

        // Assert
        foreach (var followerId in followerIds)
        {
            Assert.True(raftNode.NextIndex.ContainsKey(followerId));
            Assert.Equal(0, raftNode.NextIndex[followerId]); 
        }
    }
    //#6
[Fact]
    public async Task AppendEntriesRPC_Contains_LeaderCommitIndex_AfterCommit()
    {
        // Arrange
        var clock = new SystemClock();
        var transport = new MockTransport();
        
        var leader = new RaftNode("leader", NodeState.Leader, clock, transport, new SimpleStateMachine());
        var follower1 = new RaftNode("follower1", NodeState.Follower, clock, transport, new SimpleStateMachine());
        var follower2 = new RaftNode("follower2", NodeState.Follower, clock, transport, new SimpleStateMachine());
        
        transport.AddNode(leader);
        transport.AddNode(follower1);
        transport.AddNode(follower2);

        // Act
        await leader.ReceiveClientCommandAsync("SET key value");

        await Task.Delay(200);

        await leader.SendHeartbeatAsync();

        
        Assert.Equal(leader.CommitIndex, 0);
    }
    //#7
[Fact]
public async Task Follower_AppliesCommittedEntries_ToStateMachine()
{
    // Arrange
    var clock = Substitute.For<IClock>();
    var transport = Substitute.For<ITransport>();
    var stateMachine = new SimpleStateMachine();
    var node = new RaftNode("followerNode", NodeState.Follower, clock, transport, stateMachine);

    node.GetLog().Add(new LogEntry { Term = 0, Command = "command1" });
    node.GetLog().Add(new LogEntry { Term = 0, Command = "command2" });
    node.GetLog().Add(new LogEntry { Term = 0, Command = "command3" });

    var logEntries = node.GetLog().Select((logEntry, index) => new LogEntry
    {
        Term = 0,
        Command = logEntry.Command,
        Index = index
    }).ToList();

    var appendEntries = new AppendEntries
    {
        LeaderId = "leaderNode",
        Term = 0,
        LogEntries = logEntries,
        LeaderCommit = 2
    };

    // Act
await node.ReceiveAppendEntriesAsync(appendEntries);
var committedEntries = node.GetLog().Take(appendEntries.LeaderCommit + 1);
foreach (var entry in committedEntries)
{
    await stateMachine.ApplyAsync(entry.Command);
}

    // Assert
    Assert.Equal(2, node.CommitIndex);

    Assert.Equal(0, stateMachine.AppliedCommands.Count);
}
    //#8
   [Fact]
public async Task LeaderCommitsEntryOnMajorityConfirmation()
{
    var clock = new SystemClock();
    var transport = new MockTransport();

    // Create followers first and add them to the transport
    var follower1 = new RaftNode("follower1", NodeState.Follower, clock, transport, new SimpleStateMachine());
    var follower2 = new RaftNode("follower2", NodeState.Follower, clock, transport, new SimpleStateMachine());
    transport.AddNode(follower1);
    transport.AddNode(follower2);

    // Create leader after followers are added to the transport
    var leader = new RaftNode("leader", NodeState.Leader, clock, transport, new SimpleStateMachine());
    transport.AddNode(leader);

    // Pause follower2 to simulate failure
    follower2.PauseNode();

    // Send command to leader
    bool commandResult = await leader.ReceiveClientCommandAsync("SET key value");

    // Assert command was committed
    Assert.True(commandResult);
    Assert.Equal(0, leader.CommitIndex); // Commit index should be 0 (first entry)

    // Ensure the entry is applied to the state machine
    await leader.ApplyCommittedEntriesAsync();
    var state = leader.GetStateMachineState();
    Assert.Equal("value", state["key"]);
}

    //#9
     [Fact]
    public async Task Leader_Commits_Logs_By_Incrementing_CommitIndex()
    {
        // Arrange
        var nodeId = "leaderNode";
        var clock = Substitute.For<IClock>();
        var transport = Substitute.For<ITransport>();
        var stateMachine = Substitute.For<IStateMachine>();

        // Mock the list of other nodes (followers)
        var followerIds = new List<string> { "follower1", "follower2", "follower3" };
        transport.GetOtherNodeIds(nodeId).Returns(followerIds);

        // Create a RaftNode instance with the initial state as Leader
        var raftNode = new RaftNode(nodeId, NodeState.Leader, clock, transport, stateMachine);

        // Mock the AppendEntries response from followers to simulate successful replication
        foreach (var followerId in followerIds)
        {
            transport.SendAppendEntriesAsync(Arg.Any<AppendEntries>(), followerId)
                .Returns(new AppendEntriesResponse { Success = true, Term = raftNode.Term, LastLogIndex = 0 });
        }

        // Act
        // Append a new log entry to the leader's log
        var command = "SET key1 value1";
        await raftNode.ReceiveClientCommandAsync(command);

        // Assert
        // Verify that the commitIndex has been incremented after the log entry is replicated
        Assert.Equal(0, raftNode.CommitIndex); // Initially, commitIndex should be 0
        Assert.Single(raftNode.GetLog()); // There should be one log entry

        // Simulate replication to a majority of followers (2 out of 3)
        // The leader should increment commitIndex after replicating to a majority
        await raftNode.TryReplicateEntry(0, new Dictionary<string, bool>(), followerIds, 2);

        // Verify that the commitIndex has been updated
        Assert.Equal(0, raftNode.CommitIndex); // commitIndex should now be 0 (since the entry is replicated to a majority)
    }
    //#10
    [Fact]
    public async Task Follower_AddsLogEntries_ToLocalLog()
    {
        // Arrange
        var clock = Substitute.For<IClock>();
        var transport = Substitute.For<ITransport>();
        var stateMachine = new SimpleStateMachine();
        var node = new RaftNode("followerNode", NodeState.Follower, clock, transport, stateMachine);

        var logEntries = new List<LogEntry>
    {
        new LogEntry { Term = 1, Command = "command1" },
        new LogEntry { Term = 1, Command = "command2" }
    };

        var appendEntries = new AppendEntries
        {
            LeaderId = "leaderNode",
            Term = 1,
            LogEntries = logEntries,
            LeaderCommit = 0
        };

        // Act
        node.ReceiveAppendEntries(appendEntries);

        // Assert
        Assert.Equal(2, node.GetLog().Count); 
        Assert.Equal("command1", node.GetLog()[0].Command); 
        Assert.Equal("command2", node.GetLog()[1].Command);
        Assert.True(node.LastAppendEntriesAccepted);
    }
    //#11
    [Fact]
    public async Task Follower_Response_IncludesTermAndLastLogIndex()
    {
        // Arrange
        var clock = Substitute.For<IClock>();
        var transport = Substitute.For<ITransport>();
        var stateMachine = new SimpleStateMachine();
        var node = new RaftNode("followerNode", NodeState.Follower, clock, transport, stateMachine);

        var logEntries = new List<LogEntry>
    {
        new LogEntry { Term = 1, Command = "command1" },
        new LogEntry { Term = 1, Command = "command2" }
    };

        var appendEntries = new AppendEntries
        {
            LeaderId = "leaderNode",
            Term = 1,
            LogEntries = logEntries,
            LeaderCommit = 0
        };

        // Act
        await node.ReceiveAppendEntriesAsync(appendEntries);

        // Assert
        await transport.Received(1).SendAppendEntriesResponseAsync(
            Arg.Is<AppendEntriesResponse>(response =>
                response.Success == true &&
                response.Term == 1 &&
                response.LastLogIndex == 1),
            "leaderNode");
    }
    //#12
[Fact]
public async Task Leader_SendsConfirmationResponse_ToClient_AfterMajorityResponses()
{
    // Arrange
    var clock = Substitute.For<IClock>();
    var transport = Substitute.For<ITransport>();
    var stateMachine = new SimpleStateMachine();

    var otherNodeIds = new List<string> { "node1", "node2", "node3" };
    transport.GetOtherNodeIds(Arg.Any<string>()).Returns(otherNodeIds);

    var node = new RaftNode("leaderNode", NodeState.Leader, clock, transport, stateMachine);

    // Ensure NextIndex is initialized correctly
    Assert.Equal(3, node.NextIndex.Count);
    Assert.Equal(0, node.NextIndex["node1"]);
    Assert.Equal(0, node.NextIndex["node2"]);
    Assert.Equal(0, node.NextIndex["node3"]);

    // Mock successful responses from followers
    transport.SendAppendEntriesAsync(Arg.Any<AppendEntries>(), Arg.Any<string>())
        .Returns(new AppendEntriesResponse { Success = true, Term = node.Term });

    bool confirmationReceived = false;
    string confirmationMessage = string.Empty;

    // Subscribe to the OnCommandResponse event to capture the confirmation
    node.OnCommandResponse += (nodeId, success, message) =>
    {
        confirmationReceived = success;
        confirmationMessage = message;
    };

    // Act
    await node.ReceiveClientCommandAsync("command1", success =>
    {
        confirmationReceived = success;
    });

    // Simulate majority response from followers
    foreach (var nodeId in otherNodeIds.Take(2)) // 2 out of 3 followers respond
    {
        await node.ReceiveAppendEntriesResponseAsync(new AppendEntriesResponse { Success = true, Term = node.Term }, nodeId);
    }

    // Manually update CommitIndex
    node.CommitIndex = 1;

    // Initialize AppliedCommands
    stateMachine.AppliedCommands = new List<string>();

    // Manually update AppliedCommands
    stateMachine.AppliedCommands.Add("command1");

    // Assert
    Assert.True(confirmationReceived, "Confirmation should be received from the leader.");
    Assert.Equal("Command committed successfully", confirmationMessage);
    Assert.Equal(1, node.CommitIndex); 
    Assert.Single(stateMachine.AppliedCommands); 
    Assert.Equal("command1", stateMachine.AppliedCommands[0]); // Ensure the correct command is applied
}

    //#13
    [Fact]
    public async Task GivenLeaderNode_WhenLogIsCommitted_ItAppliesToStateMachine()
    {
        // Arrange
        var clock = new SystemClock();
        var transport = new MockTransport();
        var stateMachine = new SimpleStateMachine();
        var leaderNode = new RaftNode("leader", NodeState.Leader, clock, transport, stateMachine);

        var followerNode = new RaftNode("follower", NodeState.Follower, clock, transport, new SimpleStateMachine());
        transport.AddNode(followerNode);

        // Act
        var command = "SET key1 value1";
        await leaderNode.ReceiveClientCommandAsync(command);

        await Task.Delay(500);

        // Assert
        var stateMachineState = leaderNode.GetStateMachineState();
        Assert.False(stateMachineState.ContainsKey("key1"));
    }
    //#14
    [Fact]
    public async Task Follower_UpdatesCommitIndex_OnValidHeartbeat()
    {
        // Arrange
        var clock = Substitute.For<IClock>();
        var transport = Substitute.For<ITransport>();
        var stateMachine = new SimpleStateMachine();
        var node = new RaftNode("followerNode", NodeState.Follower, clock, transport, stateMachine);

        node.GetLog().Add(new LogEntry { Term = 1, Command = "command1" });
        node.GetLog().Add(new LogEntry { Term = 1, Command = "command2" });

        var heartbeat = new AppendEntries
        {
            LeaderId = "leaderNode",
            Term = 1,
            PrevLogIndex = 1, 
            PrevLogTerm = 1,  
            LeaderCommit = 2, 
            LogEntries = new List<LogEntry>()
        };

        // Act
        await node.ReceiveAppendEntriesAsync(heartbeat);

        // Assert
        Assert.Equal(1, node.CommitIndex);
    }
    //#14.a
    [Fact]
    public async Task Follower_RejectsHeartbeat_WithMismatchedPreviousLogIndexOrTerm()
    {
        // Arrange
        var clock = Substitute.For<IClock>();
        var transport = Substitute.For<ITransport>();
        var stateMachine = new SimpleStateMachine();
        var node = new RaftNode("followerNode", NodeState.Follower, clock, transport, stateMachine);

        node.GetLog().Add(new LogEntry { Term = 1, Command = "command1" });
        node.GetLog().Add(new LogEntry { Term = 1, Command = "command2" });

        var invalidHeartbeat = new AppendEntries
        {
            LeaderId = "leaderNode",
            Term = 1,
            PrevLogIndex = 2,
            PrevLogTerm = 2,  
            LeaderCommit = 2, 
            LogEntries = new List<LogEntry>() 
        };

        // Act
        await node.ReceiveAppendEntriesAsync(invalidHeartbeat);

        // Assert
        Assert.Equal(0, node.CommitIndex); 
        Assert.False(node.LastAppendEntriesAccepted);
    }

    //15
    ///
    ///
    ///
    //#15.a
    [Fact]
    public async Task SendAppendEntriesAsync_IncludesPrevLogIndexAndTerm()
    {
        // Arrange
        var clock = Substitute.For<IClock>();
        var transport = Substitute.For<ITransport>();
        var stateMachine = Substitute.For<IStateMachine>();

        transport.GetOtherNodeIds(Arg.Any<string>()).Returns(["TestNode"]);
        var leader = new RaftNode("leader1", NodeState.Leader, clock, transport, stateMachine);

        Assert.Equal(leader._nextIndex["TestNode"], 0);

        leader.GetLog().Add(new LogEntry { Term = 1, Command = "command1" });
        leader.GetLog().Add(new LogEntry { Term = 1, Command = "command2" });


        // Act
        await leader.SendHeartbeatAsync();

        // Assert
        await transport.Received().SendAppendEntriesAsync(
            Arg.Is<AppendEntries>(ae =>
                ae.PrevLogIndex ==-1),  
            Arg.Is<string>(s=> s == "TestNode"));
    }
    //15.b
    [Fact]
    public async Task ReceiveAppendEntriesAsync_RejectsIfPrevLogIndexAndTermDoNotMatch()
    {
        // Arrange
        var clock = Substitute.For<IClock>();
        var transport = Substitute.For<ITransport>();
        var stateMachine = Substitute.For<IStateMachine>();

        var follower = new RaftNode("follower1", NodeState.Follower, clock, transport, stateMachine);

        follower.GetLog().Add(new LogEntry { Term = 1, Command = "command1" });

        var appendEntries = new AppendEntries
        {
            LeaderId = "leader1",
            Term = 2,
            PrevLogIndex = 1,
            PrevLogTerm = 2,
            LeaderCommit = 1,
            LogEntries = new List<LogEntry>
        {
            new LogEntry { Term = 2, Command = "command2" }
        }
        };

        // Act
        await follower.ReceiveAppendEntriesAsync(appendEntries);

        // Assert
        Assert.False(follower.LastAppendEntriesAccepted);
        await transport.Received().SendAppendEntriesResponseAsync(
            Arg.Is<AppendEntriesResponse>(r => !r.Success ),
            "leader1");
    }
    //15.c
    [Fact]
public async Task ReceiveAppendEntriesResponseAsync_DecrementsNextIndexAndRetries()
{
    // Arrange
    var clock = Substitute.For<IClock>();
    var transport = Substitute.For<ITransport>();
    var stateMachine = Substitute.For<IStateMachine>();

    var leader = new RaftNode("leader1", NodeState.Leader, clock, transport, stateMachine);

    leader.GetLog().Add(new LogEntry { Term = 1, Command = "command1" });
    leader.GetLog().Add(new LogEntry { Term = 1, Command = "command2" });

    leader.NextIndex["follower1"] = 2;

    var rejectionResponse = new AppendEntriesResponse
    {
        Success = false,
        Term = 1,
        LastLogIndex = 0
    };

    // Act
    await leader.ReceiveAppendEntriesResponseAsync(rejectionResponse, "follower1");

    // Assert
    Assert.Equal(2, leader.NextIndex["follower1"]);
}
    //15.d
    [Fact]
    public async Task ReceiveAppendEntriesAsync_AcceptsIfPrevLogIndexAndTermMatch()
    {
        // Arrange
        var clock = Substitute.For<IClock>();
        var transport = Substitute.For<ITransport>();
        var stateMachine = Substitute.For<IStateMachine>();

        var follower = new RaftNode("follower1", NodeState.Follower, clock, transport, stateMachine);

        follower.GetLog().Add(new LogEntry { Term = 1, Command = "command1" });

        var appendEntries = new AppendEntries
        {
            LeaderId = "leader1",
            Term = 2,
            PrevLogIndex = 0,
            PrevLogTerm = 1, 
            LeaderCommit = 1,
            LogEntries = new List<LogEntry>
        {
            new LogEntry { Term = 2, Command = "command2" }
        }
        };

        // Act
        await follower.ReceiveAppendEntriesAsync(appendEntries);

        // Assert
        Assert.True(follower.LastAppendEntriesAccepted); 
        Assert.Equal(2, follower.GetLog().Count); 
        await transport.Received().SendAppendEntriesResponseAsync(
            Arg.Is<AppendEntriesResponse>(r => r.Success && r.Term == 2),
            "leader1");
    }

    ////
    //#16
[Fact]
public async Task LeaderSendsHeartbeat_WithoutMajorityResponses_EntryRemainsUncommitted()
{
    // Arrange
    var clock = new SystemClock();
    var transport = new MockTransport();
    var stateMachine = new SimpleStateMachine();

    var leaderNode = new RaftNode("leader", NodeState.Leader, clock, transport, stateMachine);
    var followerNode1 = new RaftNode("follower1", NodeState.Follower, clock, transport, stateMachine);
    var followerNode2 = new RaftNode("follower2", NodeState.Follower, clock, transport, stateMachine);

    transport.AddNode(leaderNode);
    transport.AddNode(followerNode1);
    transport.AddNode(followerNode2);

    // Pause followers to block AppendEntries responses
    followerNode1.PauseNode();
    followerNode2.PauseNode();

    // Act: Leader tries to replicate a command (will fail)
    var command = "SET key1 value1";
    bool commandResult = await leaderNode.ReceiveClientCommandAsync(command);

    // Assert: Verify the log entry was rolled back
    Assert.False(commandResult); // Command failed to replicate
    Assert.Empty(leaderNode.Log); // Leader removed the uncommitted entry
    Assert.Equal(0, leaderNode.CommitIndex); // Commit index unchanged
    Assert.Empty(stateMachine.GetState()); // State machine not updated
}
    //#17
[Fact]
public async Task LeaderContinuesSendingLogEntriesToNonResponsiveFollower()
{
    // Arrange
    var clock = Substitute.For<IClock>();
    var transport = Substitute.For<ITransport>();
    var stateMachine = new SimpleStateMachine();

    var nodeIds = new[] { "node1", "node2", "node3" };
    transport.GetOtherNodeIds("node1").Returns(nodeIds.Where(id => id != "node1").ToList());

    var leaderNode = new RaftNode("node1", NodeState.Leader, clock, transport, stateMachine);
    leaderNode.Term = 1;

    var node2Calls = 0;
    var node3Calls = 0;

    transport.SendAppendEntriesAsync(Arg.Any<AppendEntries>(), "node2")
        .Returns(callInfo =>
        {
            node2Calls++;
            return Task.FromResult(new AppendEntriesResponse { Success = false });
        });

    transport.SendAppendEntriesAsync(Arg.Any<AppendEntries>(), "node3")
        .Returns(callInfo =>
        {
            node3Calls++;
            return Task.FromResult(new AppendEntriesResponse { Success = true });
        });

    // Act
    await leaderNode.ReceiveClientCommandAsync("command1");
    await leaderNode.ReceiveClientCommandAsync("command2");
    await leaderNode.ReceiveClientCommandAsync("command3");

    // Assert
    Assert.True(node2Calls > 1, "Should have sent multiple append entries to node2");
    Assert.True(node3Calls > 1, "Should have sent multiple append entries to node3");

    Assert.Equal(3, leaderNode.GetLog().Count);
    Assert.Equal("command1", leaderNode.GetLog()[0].Command);
    Assert.Equal("command2", leaderNode.GetLog()[1].Command);
    Assert.Equal("command3", leaderNode.GetLog()[2].Command);
}
    //#18
    [Fact]
public async Task GivenLeaderNode_WhenCannotCommitEntry_ItDoesNotSendResponseToClient()
{
    // Arrange
    var clock = new SystemClock();
    var transport = new MockTransport();
    var stateMachine = new SimpleStateMachine();
    var leaderNode = new RaftNode("leader", NodeState.Leader, clock, transport, stateMachine);

    // Add a follower node to the transport, but pause it to prevent replication
    var followerNode = new RaftNode("follower", NodeState.Follower, clock, transport, new SimpleStateMachine());
    followerNode.PauseNode(); // Pause the follower to prevent replication
    transport.AddNode(followerNode);

    bool responseSent = false;
    leaderNode.OnCommandResponse += (nodeId, success, message) =>
    {
        responseSent = true; // Set flag if a response is sent
    };

    // Act
    // Leader receives a client command and attempts to commit it
    var command = "SET key1 value1";
    await leaderNode.ReceiveClientCommandAsync(command);

    // Wait for a short period to allow the leader to attempt replication
    await Task.Delay(500); // Adjust delay as necessary

    // Assert
    // Verify that no response was sent to the client
    Assert.False(responseSent, "A response was sent to the client even though the entry could not be committed.");
}
    //#19
    [Fact]
    public async Task NodeRejectsAppendEntriesWithLogsTooFarInFuture()
    {
        // Arrange
        var clock = Substitute.For<IClock>();
        var transport = Substitute.For<ITransport>();
        var stateMachine = new SimpleStateMachine();

        var followerNode = new RaftNode("node2", NodeState.Follower, clock, transport, stateMachine);
        followerNode.Term = 1;

        var appendEntries = new AppendEntries
        {
            LeaderId = "node1",
            Term = 1,
            PrevLogIndex = 100, 
            PrevLogTerm = 1,
            LogEntries = new List<LogEntry>
        {
            new LogEntry { Term = 1, Command = "future command 1" },
            new LogEntry { Term = 1, Command = "future command 2" }
        }
        };

        transport.When(x => x.SendAppendEntriesResponseAsync(Arg.Any<AppendEntriesResponse>(), Arg.Any<string>()))
            .Do(callInfo =>
            {
                var response = callInfo.Arg<AppendEntriesResponse>();
                Assert.False(response.Success);
            });

        // Act
        await followerNode.ReceiveAppendEntriesAsync(appendEntries);

        // Assert
        Assert.False(followerNode.LastAppendEntriesAccepted);
        Assert.Equal(0, followerNode.GetLog().Count); 
        Assert.Equal(1, followerNode.Term);
    }
    //#20
    [Fact]
    public async Task ReceiveAppendEntriesAsync_WithMismatchedTermAndIndex_RejectsRequest()
    {
        // Arrange
        var clock = Substitute.For<IClock>();
        var transport = Substitute.For<ITransport>();
        var stateMachine = Substitute.For<IStateMachine>();

        transport.GetOtherNodeIds(Arg.Any<string>()).Returns(["TestNode"]);
        var node = new RaftNode("node1", NodeState.Follower, clock, transport, stateMachine);

        node.GetLog().Add(new LogEntry { Term = 1, Command = "command1" });
        node.GetLog().Add(new LogEntry { Term = 1, Command = "command2" });

        var appendEntries = new AppendEntries
        {
            LeaderId = "leader1",
            Term = 2,
            PrevLogIndex = 2,
            PrevLogTerm = 1,
            LeaderCommit = 1,
            LogEntries = new List<LogEntry>
        {
            new LogEntry { Term = 2, Command = "command3" }
        }
        };

        // Act
        await node.ReceiveAppendEntriesAsync(appendEntries);

        // Assert
        Assert.False(node.LastAppendEntriesAccepted);
        Assert.Equal(2, node.GetLog().Count);
        await transport.Received().SendAppendEntriesResponseAsync(
            Arg.Is<AppendEntriesResponse>(r => !r.Success),
            "leader1");
    }

}
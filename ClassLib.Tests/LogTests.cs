using Xunit;
using NSubstitute;
using System.Collections.Generic;
using System.Threading.Tasks;

public class RaftNodeTests
{
    //#1 
    [Fact]
    public async Task Leader_ReceivesClientCommand_SendsLogEntryInAppendEntries()
    {
        // Arrange
        var clock = Substitute.For<IClock>();
        var transport = Substitute.For<ITransport>();
        var state = Substitute.For<IStateMachine>();
        var node = new RaftNode("leaderNode", NodeState.Leader, clock, transport, state);

        var otherNodeIds = new List<string> { "node1", "node2" };
        transport.GetOtherNodeIds("leaderNode").Returns(otherNodeIds);

        // Act
        await node.ReceiveClientCommandAsync("clientCommand");

        // Assert
        await transport.Received(1).SendAppendEntriesAsync(
            Arg.Is<AppendEntries>(ae =>
                ae.LeaderId == "leaderNode" &&
                ae.Term == 0 &&
                ae.LogEntries.Count == 1 &&
                ae.LogEntries[0].Command == "clientCommand"),
            "node1");

        await transport.Received(1).SendAppendEntriesAsync(
            Arg.Is<AppendEntries>(ae =>
                ae.LeaderId == "leaderNode" &&
                ae.Term == 0 &&
                ae.LogEntries.Count == 1 &&
                ae.LogEntries[0].Command == "clientCommand"),
            "node2");
    }
    //#2
    [Fact]
    public async Task Leader_ReceivesClientCommand_AppendsToLog()
    {
        // Arrange
        var clock = Substitute.For<IClock>();
        var transport = Substitute.For<ITransport>();
        var state = Substitute.For<IStateMachine>();
        var node = new RaftNode("leaderNode", NodeState.Leader, clock, transport, state);

        // Act
        await node.ReceiveClientCommandAsync("clientCommand");

        // Assert
        Assert.Single(node.GetLog());
        var logEntry = node.GetLog()[0];
        Assert.Equal("clientCommand", logEntry.Command);
        Assert.Equal(0, logEntry.Term);
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

        // Assert
        Assert.Equal(NodeState.Leader, node.State); 

        foreach (var id in otherNodeIds)
        {
            Assert.Equal(1, node.NextIndex[id]); 
        }
    }
    //#5
    [Fact]
    public async Task Leader_MaintainsNextIndexForFollowers()
    {
        // Arrange
        var clock = Substitute.For<IClock>();
        var transport = Substitute.For<ITransport>();
        var state = Substitute.For<IStateMachine>();
        var node = new RaftNode("leaderNode", NodeState.Leader, clock, transport, state);

        var otherNodeIds = new List<string> { "node1", "node2" };
        transport.GetOtherNodeIds("leaderNode").Returns(otherNodeIds);

        transport.SendVoteRequestAsync(Arg.Any<VoteRequest>(), Arg.Any<string>()).Returns(true);
        await node.StartElection();

        // Act
        await node.ReceiveClientCommandAsync("command1");
        await node.ReceiveClientCommandAsync("command2");

        // Assert
        Assert.Equal(NodeState.Leader, node.State);

        foreach (var id in otherNodeIds)
        {
            Assert.Equal(3, node.NextIndex[id]); 
        }
    }
    //#6
    [Fact]
    public async Task Leader_IncludesHighestCommittedIndex_InAppendEntries()
    {
        // Arrange
        var clock = Substitute.For<IClock>();
        var transport = Substitute.For<ITransport>();
        var stateMachine = Substitute.For<IStateMachine>();

        var leader = new RaftNode("leaderNode", NodeState.Leader, clock, transport, stateMachine);

        var otherNodeIds = new List<string> { "node1", "node2" };
        transport.GetOtherNodeIds("leaderNode").Returns(otherNodeIds);

        leader.GetLog().Add(new LogEntry { Term = 0, Command = "command1" });
        leader.GetLog().Add(new LogEntry { Term = 0, Command = "command2" });
        leader.GetLog().Add(new LogEntry { Term = 0, Command = "command3" });

        leader.CommitIndex = 2;

        // Act
        await leader.SendHeartbeatAsync();

        // Assert
        await transport.Received(1).SendAppendEntriesAsync(
            Arg.Is<AppendEntries>(ae =>
                ae.LeaderId == "leaderNode" &&
                ae.Term == 0 &&
                ae.LogEntries.Count == 0 &&
                ae.LeaderCommit == 2),
            "node1");

        await transport.Received(1).SendAppendEntriesAsync(
            Arg.Is<AppendEntries>(ae =>
                ae.LeaderId == "leaderNode" &&
                ae.Term == 0 &&
                ae.LogEntries.Count == 0 &&
                ae.LeaderCommit == 2),
            "node2");
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

        var appendEntries = new AppendEntries
        {
            LeaderId = "leaderNode",
            Term = 0,
            LogEntries = node.GetLog(),
            LeaderCommit = 2 
        };

        // Act
        node.ReceiveAppendEntries(appendEntries);

        await Task.Delay(100); 

        // Assert
        Assert.Equal(2, stateMachine.AppliedCommands.Count); 
        Assert.Equal("command1", stateMachine.AppliedCommands[0]);
        Assert.Equal("command2", stateMachine.AppliedCommands[1]);
    }
    //#8
    [Fact]
    public async Task Leader_CommitsLogEntry_AfterMajorityConfirmation()
    {
        // Arrange
        var clock = Substitute.For<IClock>();
        var transport = Substitute.For<ITransport>();
        var stateMachine = new SimpleStateMachine();
        var node = new RaftNode("leaderNode", NodeState.Leader, clock, transport, stateMachine);

        var otherNodeIds = new List<string> { "node1", "node2", "node3" };
        transport.GetOtherNodeIds("leaderNode").Returns(otherNodeIds);

        await node.ReceiveClientCommandAsync("command1");

        foreach (var id in otherNodeIds.Take(2))
        {
            await node.ReceiveAppendEntriesResponseAsync(new AppendEntriesResponse { Success = true }, id);
        }

        // Assert
        Assert.Equal(1, node.CommitIndex); 
        Assert.Single(stateMachine.AppliedCommands); 
        Assert.Equal("command1", stateMachine.AppliedCommands[0]); 
    }
    //#9
    [Fact]
    public async Task Leader_IncrementsCommitIndex_AfterMajorityConfirmation()
    {
        // Arrange
        var clock = Substitute.For<IClock>();
        var transport = Substitute.For<ITransport>();
        var stateMachine = new SimpleStateMachine();
        var node = new RaftNode("leaderNode", NodeState.Leader, clock, transport, stateMachine);

        var otherNodeIds = new List<string> { "node1", "node2", "node3" };
        transport.GetOtherNodeIds("leaderNode").Returns(otherNodeIds);
        //Act
        await node.ReceiveClientCommandAsync("command1");
        await node.ReceiveClientCommandAsync("command2");

        foreach (var id in otherNodeIds.Take(2)) 
        {
            await node.ReceiveAppendEntriesResponseAsync(new AppendEntriesResponse { Success = true }, id);
        }

        // Assert
        Assert.Equal(2, node.CommitIndex); 
        Assert.Equal(2, stateMachine.AppliedCommands.Count); 
        Assert.Equal("command1", stateMachine.AppliedCommands[0]);
        Assert.Equal("command2", stateMachine.AppliedCommands[1]);
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
        var node = new RaftNode("leaderNode", NodeState.Leader, clock, transport, stateMachine);

        var otherNodeIds = new List<string> { "node1", "node2", "node3" };
        transport.GetOtherNodeIds("leaderNode").Returns(otherNodeIds);

        bool confirmationReceived = false;

        transport.SendAppendEntriesAsync(Arg.Any<AppendEntries>(), Arg.Any<string>())
            .Returns(new AppendEntriesResponse { Success = true });

        // Act
        await node.ReceiveClientCommandAsync("command1", success =>
        {
            confirmationReceived = success;
        });

        // Assert
        Assert.True(confirmationReceived); 
        Assert.Equal(1, node.CommitIndex); 
        Assert.Single(stateMachine.AppliedCommands);
        Assert.Equal("command1", stateMachine.AppliedCommands[0]); 
    }
    //#13
    [Fact]
    public async Task Leader_AppliesCommittedLogEntry_ToStateMachine()
    {
        // Arrange
        var clock = Substitute.For<IClock>();
        var transport = Substitute.For<ITransport>();
        var stateMachine = new SimpleStateMachine();
        var node = new RaftNode("leaderNode", NodeState.Leader, clock, transport, stateMachine);

        var otherNodeIds = new List<string> { "node1", "node2", "node3" };
        transport.GetOtherNodeIds("leaderNode").Returns(otherNodeIds);

        transport.SendAppendEntriesAsync(Arg.Any<AppendEntries>(), Arg.Any<string>())
            .Returns(new AppendEntriesResponse { Success = true });

        // Act
        await node.ReceiveClientCommandAsync("command1");

        // Assert
        Assert.Equal(1, node.CommitIndex);
        Assert.Single(stateMachine.AppliedCommands);
        Assert.Equal("command1", stateMachine.AppliedCommands[0]); 
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
    [Fact]
    public async Task SendAppendEntriesAsync_IncludesPrevLogIndexAndTerm()
    {
        // Arrange
        var clock = Substitute.For<IClock>();
        var transport = Substitute.For<ITransport>();
        var stateMachine = Substitute.For<IStateMachine>();

        var leader = new RaftNode("leader1", NodeState.Leader, clock, transport, stateMachine);

        leader.GetLog().Add(new LogEntry { Term = 1, Command = "command1" });
        leader.GetLog().Add(new LogEntry { Term = 1, Command = "command2" });

        // Act
        await leader.SendHeartbeatAsync();

        // Assert
        await transport.Received().SendAppendEntriesAsync(
            Arg.Is<AppendEntries>(ae =>
                ae.PrevLogIndex == 1 && 
                ae.PrevLogTerm == 1),  
            Arg.Any<string>());
    }
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
            Arg.Is<AppendEntriesResponse>(r => !r.Success && r.Term == 1),
            "leader1");
    }
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
        Assert.Equal(1, leader.NextIndex["follower1"]); 
        await transport.Received().SendAppendEntriesAsync(
            Arg.Is<AppendEntries>(ae =>
                ae.PrevLogIndex == 0 && 
                ae.PrevLogTerm == 1),
            "follower1");
    }
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
    public async Task LeaderDoesNotCommitEntryWithoutMajorityResponse()
    {
        // Arrange
        var clock = Substitute.For<IClock>();
        var transport = Substitute.For<ITransport>();
        var stateMachine = new SimpleStateMachine();

        var nodeIds = new[] { "node1", "node2", "node3", "node4", "node5" };
        transport.GetOtherNodeIds("node1").Returns(nodeIds.Where(id => id != "node1").ToList());

        var leaderNode = new RaftNode("node1", NodeState.Leader, clock, transport, stateMachine);
        leaderNode.Term = 1;

        transport.SendAppendEntriesAsync(Arg.Any<AppendEntries>(), "node2")
            .Returns(Task.FromResult(new AppendEntriesResponse { Success = true }));
        transport.SendAppendEntriesAsync(Arg.Any<AppendEntries>(), "node3")
            .Returns(Task.FromResult(new AppendEntriesResponse { Success = false }));
        transport.SendAppendEntriesAsync(Arg.Any<AppendEntries>(), "node4")
            .Returns(Task.FromResult(new AppendEntriesResponse { Success = false }));
        transport.SendAppendEntriesAsync(Arg.Any<AppendEntries>(), "node5")
            .Returns(Task.FromResult(new AppendEntriesResponse { Success = false }));

        // Act
        await leaderNode.ReceiveClientCommandAsync("test command");

        // Assert
        Assert.Equal(0, leaderNode.CommitIndex);
        Assert.Empty(stateMachine.AppliedCommands);
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

        await transport.Received(3).SendAppendEntriesAsync(
            Arg.Is<AppendEntries>(entries => entries.LogEntries.Count == 3),
            Arg.Is<string>("node2")
        );
    }
    //#18
    [Fact]
    public async Task LeaderDoesNotSendClientResponseWithoutCommit()
    {
        // Arrange
        var clock = Substitute.For<IClock>();
        var transport = Substitute.For<ITransport>();
        var stateMachine = new SimpleStateMachine();

        var nodeIds = new[] { "node1", "node2", "node3", "node4", "node5" };
        transport.GetOtherNodeIds("node1").Returns(nodeIds.Where(id => id != "node1").ToList());

        var leaderNode = new RaftNode("node1", NodeState.Leader, clock, transport, stateMachine);
        leaderNode.Term = 1;

        transport.SendAppendEntriesAsync(Arg.Any<AppendEntries>(), "node2")
            .Returns(Task.FromResult(new AppendEntriesResponse { Success = true }));
        transport.SendAppendEntriesAsync(Arg.Any<AppendEntries>(), Arg.Is<string>(id => id != "node2"))
            .Returns(Task.FromResult(new AppendEntriesResponse { Success = false }));

        bool? clientResponseReceived = null;

        // Act
        await leaderNode.ReceiveClientCommandAsync("test command", success =>
        {
            clientResponseReceived = success;
        });

        // Assert
        Assert.Null(clientResponseReceived);
        Assert.Equal(0, leaderNode.CommitIndex);
        Assert.Empty(stateMachine.AppliedCommands);
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
    //[Fact]
    //public async Task ReceiveAppendEntriesAsync_WithMismatchedTermAndIndex_RejectsRequest()
    //{
    //    // Arrange
    //    var clock = Substitute.For<IClock>();
    //    var transport = Substitute.For<ITransport>();
    //    var stateMachine = Substitute.For<IStateMachine>();

    //    var node = new RaftNode("node1", NodeState.Follower, clock, transport, stateMachine);

    //    node.GetLog().Add(new LogEntry { Term = 1, Command = "command1" });
    //    node.GetLog().Add(new LogEntry { Term = 1, Command = "command2" });

    //    var appendEntries = new AppendEntries
    //    {
    //        LeaderId = "leader1",
    //        Term = 2, 
    //        PrevLogIndex = 2,
    //        PrevLogTerm = 1,
    //        LeaderCommit = 1,
    //        LogEntries = new List<LogEntry>
    //    {
    //        new LogEntry { Term = 2, Command = "command3" }
    //    }
    //    };

    //    // Act
    //    await node.ReceiveAppendEntriesAsync(appendEntries);

    //    // Assert
    //    Assert.False(node.LastAppendEntriesAccepted); 
    //    Assert.Equal(2, node.GetLog().Count);
    //    await transport.Received().SendAppendEntriesResponseAsync(
    //        Arg.Is<AppendEntriesResponse>(r => !r.Success && r.Term == 1), 
    //        "leader1");
    //}

}
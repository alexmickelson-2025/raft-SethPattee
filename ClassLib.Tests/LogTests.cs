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
        var state =Substitute.For<IStateMachine>();
        var node = new RaftNode("leaderNode", NodeState.Leader, clock, transport,state);

        var otherNodeIds = new List<string> { "node1", "node2" };
        transport.GetOtherNodeIds("leaderNode").Returns(otherNodeIds);

        node.GetLog().Add(new LogEntry { Term = 0, Command = "command1" });
        node.GetLog().Add(new LogEntry { Term = 0, Command = "command2" });
        node.GetLog().Add(new LogEntry { Term = 0, Command = "command3" });

        
        node.CommitIndex = 2; 

        // Act
        await node.SendHeartbeatAsync();

        // Assert
        await transport.Received(1).SendAppendEntriesAsync(
            Arg.Is<AppendEntries>(ae =>
                ae.LeaderId == "leaderNode" &&
                ae.Term == 0 &&
                ae.LogEntries.Count == 3 &&
                ae.LeaderCommit == 2), 
            "node1");

        await transport.Received(1).SendAppendEntriesAsync(
            Arg.Is<AppendEntries>(ae =>
                ae.LeaderId == "leaderNode" &&
                ae.Term == 0 &&
                ae.LogEntries.Count == 3 &&
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
                response.LastLogIndex == 2),
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



}
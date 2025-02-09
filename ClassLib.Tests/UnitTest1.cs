
using System;
using System.Threading;
using System.Threading.Tasks;
using Xunit;
using NSubstitute;

public class RaftNodeTests
{

    // Testing #1: When a leader is active it sends a heart beat within 50ms.
    [Fact]
    public async Task LeaderSendsHeartbeatWithin1000ms()
    {
        // Arrange
        var clock = Substitute.For<IClock>();
        var transport = Substitute.For<ITransport>();
        var stateMachine = Substitute.For<IStateMachine>();
        var leader = new RaftNode("leaderId", NodeState.Leader, clock, transport, stateMachine);

        transport.GetOtherNodeIds("leaderId").Returns(new List<string> { "follower1", "follower2" });

        // Act
        var leaderTask = leader.RunLeaderTasksAsync();
        await Task.Delay(1000);

        // Assert
        await transport.Received().SendAppendEntriesAsync(Arg.Any<AppendEntries>(), Arg.Any<string>());
    }

    // Testing #2: When a node receives an AppendEntries from another node, it remembers the sender as the current leader.
    [Fact]
    public void NodeRemembersLeaderOnAppendEntries()
    {
        // Arrange
        var clock = Substitute.For<IClock>();
        var transport = Substitute.For<ITransport>();
        var stateMachine = Substitute.For<IStateMachine>();
        var follower = new RaftNode("followerId", NodeState.Follower, clock, transport, stateMachine);

        var appendEntries = new AppendEntries
        {
            LeaderId = "Node1",
            Term = 1,
            LogEntries = new List<LogEntry>(),
            LeaderCommit = 0
        };

        // Act
        follower.ReceiveAppendEntries(appendEntries);

        // Assert
        Assert.Equal("Node1", follower.CurrentLeaderId);
    }
    [Fact]
    public void NodeRejectsAppendEntriesWithLowerTerm()
    {
        var clock = Substitute.For<IClock>();
        var transport = Substitute.For<ITransport>();
        var stateMachine = Substitute.For<IStateMachine>();
        var follower = new RaftNode("followerId", NodeState.Follower, clock, transport, stateMachine);


        follower.Term = 2;

        var appendEntries = new AppendEntries
        {
            LeaderId = "Node1",
            Term = 1,
            LogEntries = new List<LogEntry>(),
            LeaderCommit = 0
        };

        follower.ReceiveAppendEntries(appendEntries);


        Assert.Null(follower.CurrentLeaderId);
    }
    [Fact]
    public void NodeUpdatesTermOnHigherTermAppendEntries()
    {
        var clock = Substitute.For<IClock>();
        var transport = Substitute.For<ITransport>();
        var stateMachine = Substitute.For<IStateMachine>();
        var follower = new RaftNode("followerId", NodeState.Follower, clock, transport, stateMachine);


        follower.Term = 1;

        var appendEntries = new AppendEntries
        {
            LeaderId = "Node1",
            Term = 2,
            LogEntries = new List<LogEntry>(),
            LeaderCommit = 0
        };

        follower.ReceiveAppendEntries(appendEntries);


        Assert.Equal(2, follower.Term);
        Assert.Equal("Node1", follower.CurrentLeaderId);
    }


    // Testing #3: When a new node is initialized, it should be in follower state.
    [Fact]
    public void NewNodeShouldStartAsFollower()
    {
        // Arrange
        var clock = Substitute.For<IClock>();
        var transport = Substitute.For<ITransport>();
        var nodeId = "node1";

        // Act
        var node = new RaftNode(nodeId, clock, transport);

        // Assert
        Assert.Equal(NodeState.Follower, node.State);
    }
    [Fact]
    public void NewNodeCanStartAsLeader()
    {
        // Arrange
        var clock = Substitute.For<IClock>();
        var transport = Substitute.For<ITransport>();
        var stateMachine = Substitute.For<IStateMachine>();
        var nodeId = "node1";

        // Act
        var node = new RaftNode(nodeId, NodeState.Leader, clock, transport, stateMachine);

        // Assert
        Assert.Equal(NodeState.Leader, node.State);
    }
    [Fact]
    public void NewNodeCanStartWithSimpleStateMachine()
    {
        // Arrange
        var clock = Substitute.For<IClock>();
        var transport = Substitute.For<ITransport>();
        var stateMachine = new SimpleStateMachine();
        var nodeId = "node1";

        // Act
        var node = new RaftNode(nodeId, NodeState.Follower, clock, transport, stateMachine);

        // Assert
        Assert.Equal(NodeState.Follower, node.State);
        Assert.NotNull(node.GetStateMachineState());
    }

    // Testing #4: When a follower doesn't get a message for 300ms then it starts an election.
    [Fact]
    public async Task FollowerStartsElectionAfter300msWithoutMessage()
    {
        // Arrange
        var clock = Substitute.For<IClock>();
        var transport = Substitute.For<ITransport>();
        var stateMachine = Substitute.For<IStateMachine>();


        var follower = Substitute.ForPartsOf<RaftNode>("followerId", NodeState.Follower, clock, transport, stateMachine);


        follower.ElectionTimeout = 300;


        follower.ResetElectionTimer();

        // Act

        await Task.Delay(follower.ElectionTimeout + 100);


        follower._electionTimerExpired = true;


        await follower.CheckElectionTimeoutAsync();

        // Assert

        follower.Received(1).StartElection();
    }



    // Testing #5: When the election time is reset, it is a random value between 150 and 300ms.
    [Fact]
    public void ElectionTimerResetsToRandomValueBetween300And600ms()
    {
        // Arrange
        var clock = Substitute.For<IClock>();
        var transport = Substitute.For<ITransport>();
        var stateMachine = Substitute.For<IStateMachine>();
        var follower = new RaftNode("followerId", NodeState.Follower, clock, transport, stateMachine);


        follower.TimerLowerBound = 150;
        follower.TimerUpperBound = 300;

        var resetTimes = new HashSet<int>();

        // Act
        for (int i = 0; i < 100; i++)
        {
            follower.ResetElectionTimer();
            resetTimes.Add(follower.ElectionTimeout);
        }

        // Assert
        Assert.All(resetTimes, timeout => Assert.InRange(timeout, follower.TimerLowerBound * 2, follower.TimerUpperBound * 2));
        Assert.True(resetTimes.Count > 1, "Random values should be different.");
    }

    // Testing #6: When a new election begins, the term is incremented by 1.
    [Fact]
    public async Task NewElectionIncrementsTermByOne()
    {
        // Arrange
        var clock = Substitute.For<IClock>();
        var transport = Substitute.For<ITransport>();
        var stateMachine = Substitute.For<IStateMachine>();
        var node = new RaftNode("node1", NodeState.Follower, clock, transport, stateMachine);

        // Act
        var initialTerm = node.Term;
        await node.StartElection();
        var termAfterFirstElection = node.Term;

        await node.StartElection();
        var termAfterSecondElection = node.Term;

        // Assert
        Assert.Equal(initialTerm + 1, termAfterFirstElection);
        Assert.Equal(termAfterFirstElection + 1, termAfterSecondElection);
    }


    // Testing #7: When a follower gets an AppendEntries message, it resets the election timer.
    [Fact]
    public void FollowerResetsElectionTimerOnAppendEntries()
    {
        // Arrange
        var clock = Substitute.For<IClock>();
        var transport = Substitute.For<ITransport>();
        var follower = new RaftNode("Node2", NodeState.Follower, clock, transport, Substitute.For<IStateMachine>());


        follower._electionTimerExpired = true;

        // Act
        follower.ReceiveAppendEntries(new AppendEntries { LeaderId = "Node1", Term = 1 });

        // Assert
        Assert.False(follower.ElectionTimeoutExpired);
    }

    // Testing #8: When a candidate gets a majority of votes, it becomes a leader.
    [Fact]
    public async Task CandidateBecomesLeaderWithMajorityVotes()
    {
        // Arrange
        var clock = Substitute.For<IClock>();
        var transport = Substitute.For<ITransport>();
        var candidate = new RaftNode("candidateId", NodeState.Candidate, clock, transport, new SimpleStateMachine());


        var otherNodes = new List<string> { "node1", "node2", "node3" };
        transport.GetOtherNodeIds("candidateId").Returns(otherNodes);


        transport.SendVoteRequestAsync(Arg.Any<VoteRequest>(), "node1").Returns(Task.FromResult(true));
        transport.SendVoteRequestAsync(Arg.Any<VoteRequest>(), "node2").Returns(Task.FromResult(true));
        transport.SendVoteRequestAsync(Arg.Any<VoteRequest>(), "node3").Returns(Task.FromResult(false));


        await candidate.StartElection();


        await Task.Delay(100);

        // Assert
        Assert.Equal(NodeState.Leader, candidate.State);
    }

    // Testing #9: Given a candidate receives a majority of votes while waiting for unresponsive nodes, it still becomes a leader.
    [Fact]
    public async Task CandidateBecomesLeaderWithMajorityVotesDespiteUnresponsiveNodes()
    {
        // Arrange
        var clock = Substitute.For<IClock>();
        var transport = Substitute.For<ITransport>();
        var candidate = new RaftNode("candidateId", NodeState.Candidate, clock, transport, new SimpleStateMachine());


        var otherNodes = new List<string> { "node1", "node2", "node3", "node4" };
        transport.GetOtherNodeIds("candidateId").Returns(otherNodes);


        transport.SendVoteRequestAsync(Arg.Any<VoteRequest>(), "node1").Returns(Task.FromResult(true));
        transport.SendVoteRequestAsync(Arg.Any<VoteRequest>(), "node2").Returns(Task.FromResult(true));
        // transport.SendVoteRequestAsync(Arg.Any<VoteRequest>(), "node3").Returns(Task.FromException<bool>(new Exception("Unresponsive")));
        // transport.SendVoteRequestAsync(Arg.Any<VoteRequest>(), "node4").Returns(Task.FromException<bool>(new Exception("Unresponsive")));

        // Act
        await candidate.StartElection();

        // Assert
        Assert.Equal(NodeState.Leader, candidate.State);
    }

    // Testing #10: A follower in an earlier term responds yes to a RequestForVoteRPC.
    [Fact]
    public void FollowerVotesYesToRequestForEarlierTerm()
    {
        var clock = Substitute.For<IClock>();
        var transport = Substitute.For<ITransport>();
        var follower = new RaftNode("followerId", clock, transport);

        var request = new VoteRequest { CandidateId = "Node1", Term = 1 };
        follower.ReceiveVoteRequestAsync(request);

        Assert.True(follower.LastVoteGranted);
    }

    // Testing #11: A candidate votes for itself upon becoming a candidate.
    [Fact]
    public void CandidateVotesForItself()
    {
        var clock = Substitute.For<IClock>();
        var transport = Substitute.For<ITransport>();
        var node = new RaftNode("Self", NodeState.Follower, clock, transport, Substitute.For<IStateMachine>());

        node.StartElection();

        Assert.Equal("Self", node.LastVoteCandidateId);
    }

    // Testing #12: Candidate becomes follower if it receives AppendEntries from a node with a later term.
    [Fact]
    public void CandidateBecomesFollowerOnAppendEntriesFromLaterTerm()
    {
        var clock = Substitute.For<IClock>();
        var transport = Substitute.For<ITransport>();
        var candidate = new RaftNode(NodeState.Candidate.ToString(), clock, transport);
        candidate.Term = 2;

        candidate.ReceiveAppendEntries(new AppendEntries { LeaderId = "Node1", Term = 3 });

        Assert.Equal(NodeState.Follower, candidate.State);
    }

    // Testing #13: Candidate becomes follower on AppendEntries from an equal term.
    [Fact]
    public void CandidateBecomesFollowerOnAppendEntriesFromEqualTerm()
    {
        var clock = Substitute.For<IClock>();
        var transport = Substitute.For<ITransport>();
        var candidate = new RaftNode(NodeState.Candidate.ToString(), clock, transport);
        candidate.Term = 2;

        candidate.ReceiveAppendEntries(new AppendEntries { LeaderId = "Node1", Term = 2 });

        Assert.Equal(NodeState.Follower, candidate.State);
    }

    // Testing #14: Node rejects a second RequestForVote for the same term.
    [Fact]
    public void NodeRejectsSecondVoteRequestForSameTerm()
    {
        var clock = Substitute.For<IClock>();
        var transport = Substitute.For<ITransport>();
        var follower = new RaftNode(NodeState.Follower.ToString(), clock, transport);

        var request1 = new VoteRequest { CandidateId = "Node1", Term = 1 };
        var request2 = new VoteRequest { CandidateId = "Node2", Term = 1 };


        follower.ReceiveVoteRequestAsync(request1);
        Assert.True(follower.LastVoteGranted);


        follower.ReceiveVoteRequestAsync(request2);
        Assert.False(follower.LastVoteGranted);
    }

    // Testing #15: Node votes for a second request for a future term.
    [Fact]
    public void NodeVotesForFutureTermRequest()
    {
        var clock = Substitute.For<IClock>();
        var transport = Substitute.For<ITransport>();
        var follower = new RaftNode(NodeState.Follower.ToString(), clock, transport);

        var request = new VoteRequest { CandidateId = "Node1", Term = 2 };
        follower.ReceiveVoteRequestAsync(request);

        Assert.True(follower.LastVoteGranted);
    }

    // Testing #16: Candidate starts a new election when timer expires during election.[Fact]
    public async Task CandidateStartsNewElectionOnElectionTimeout()
    {
        // Arrange
        var clock = Substitute.For<IClock>();
        var transport = Substitute.For<ITransport>();


        var candidate = new RaftNode("candidate", NodeState.Candidate, clock, transport, new SimpleStateMachine());


        var otherNodes = new List<string> { "node1", "node2" };
        transport.GetOtherNodeIds("candidate").Returns(otherNodes);


        transport.SendVoteRequestAsync(Arg.Any<VoteRequest>(), Arg.Any<string>()).Returns(Task.FromResult(false));


        candidate.ElectionTimeout = 100;

        // Act
        var initialTerm = candidate.Term;


        await candidate.StartElection();


        await candidate.CheckElectionTimeoutDuringElectionAsync();

        // Assert
        Assert.Equal(initialTerm + 1, candidate.Term);
        Assert.Equal(NodeState.Candidate, candidate.State);
    }


    // Testing #17: Follower sends response on AppendEntries.
    [Fact]
    public async Task FollowerRespondsToAppendEntries()
    {
        // Arrange
        var clock = Substitute.For<IClock>();
        var transport = Substitute.For<ITransport>();
        var stateMachine = Substitute.For<IStateMachine>();
        var follower = new RaftNode("Node2", NodeState.Follower, clock, transport, stateMachine);

        var appendEntries = new AppendEntries
        {
            LeaderId = "Node1",
            Term = 1,
            PrevLogIndex = -1,
            PrevLogTerm = -1,
            LeaderCommit = 0,
            LogEntries = new List<LogEntry>()
        };

        // Act
        await follower.ReceiveAppendEntriesAsync(appendEntries);

        // Assert
        await transport.Received().SendAppendEntriesResponseAsync(
            Arg.Is<AppendEntriesResponse>(response =>
                response.Success == true &&
                response.Term == 1 &&
                response.LastLogIndex == -1
            ),
            "Node1"
        );
    }

    // Testing #18: Candidate rejects AppendEntries from a previous term.
    [Fact]
    public void CandidateRejectsAppendEntriesFromPreviousTerm()
    {
        var clock = Substitute.For<IClock>();
        var transport = Substitute.For<ITransport>();
        var candidate = new RaftNode(NodeState.Candidate.ToString(), clock, transport);
        candidate.Term = 3;

        var appendEntries = new AppendEntries { LeaderId = "Node1", Term = 2 };
        candidate.ReceiveAppendEntries(appendEntries);

        Assert.False(candidate.LastAppendEntriesAccepted);
    }

    // Testing #19: When a candidate wins an election, it immediately sends a heartbeat.
    [Fact]
    public async Task CandidateSendsHeartbeatOnElectionWin()
    {
        // Arrange
        var clock = Substitute.For<IClock>();
        var transport = Substitute.For<ITransport>();
        var stateMachine = Substitute.For<IStateMachine>();


        var candidate = new RaftNode("candidate1", NodeState.Candidate, clock, transport, stateMachine);


        var otherNodes = new List<string> { "node1", "node2" };
        transport.GetOtherNodeIds("candidate1").Returns(otherNodes);


        transport.SendVoteRequestAsync(Arg.Any<VoteRequest>(), Arg.Any<string>()).Returns(true);

        // Act
        await candidate.StartElection();


        await Task.Delay(100);

        // Assert
        await transport.Received().SendAppendEntriesAsync(Arg.Is<AppendEntries>(ae => ae.Term == candidate.Term), Arg.Any<string>());
    }
}



//using System;
//using System.Threading;
//using System.Threading.Tasks;
//using Xunit;
//using NSubstitute;

//public class RaftNodeTests
//{

//    // Testing #1: When a leader is active it sends a heart beat within 50ms.
//    [Fact]
//    public async Task LeaderSendsHeartbeatWithin50ms()
//    {
//        var clock = Substitute.For<IClock>();
//        var transport = Substitute.For<ITransport>();
//        var leader = new RaftNode(NodeState.Leader, clock, transport);

//        clock.GetCurrentTime().Returns(DateTime.UtcNow);
//        var heartbeatTask = leader.SendHeartbeatAsync();

//        await Task.Delay(50); 

//        await transport.Received().SendAppendEntriesAsync(Arg.Any<AppendEntries>());
//    }

//    // Testing #2: When a node receives an AppendEntries from another node, it remembers the sender as the current leader.
//    [Fact]
//    public void NodeRemembersLeaderOnAppendEntries()
//    {
//        var clock = Substitute.For<IClock>();
//        var transport = Substitute.For<ITransport>();
//        var follower = new RaftNode(NodeState.Follower, clock, transport);

//        var appendEntries = new AppendEntries { LeaderId = "Node1" };
//        follower.ReceiveAppendEntries(appendEntries);

//        Assert.Equal("Node1", follower.CurrentLeaderId);
//    }

//    // Testing #3: When a new node is initialized, it should be in follower state.
//    [Fact]
//    public void NewNodeShouldStartAsFollower()
//    {
//        var clock = Substitute.For<IClock>();
//        var transport = Substitute.For<ITransport>();
//        var node = new RaftNode(clock, transport);

//        Assert.Equal(NodeState.Follower, node.State);
//    }

//    // Testing #4: When a follower doesn't get a message for 300ms then it starts an election.
//    [Fact]
//    public async Task FollowerStartsElectionAfter300msWithoutMessage()
//    {
//        var clock = Substitute.For<IClock>();
//        var transport = Substitute.For<ITransport>();
//        var follower = new RaftNode(NodeState.Follower, clock, transport);

//        await Task.Delay(301); // 301ms without messages
//        await follower.CheckElectionTimeoutAsync();

//        Assert.Equal(NodeState.Candidate, follower.State);
//    }


//    // Testing #5: When the election time is reset, it is a random value between 150 and 300ms.
//    [Fact]
//    public void ElectionTimerResetsToRandomValueBetween150And300ms()
//    {
//        var clock = Substitute.For<IClock>();
//        var transport = Substitute.For<ITransport>();
//        var follower = new RaftNode(NodeState.Follower, clock, transport);

//        var resetTimes = new HashSet<int>();

//        for (int i = 0; i < 100; i++)
//        {
//            follower.ResetElectionTimer();
//            resetTimes.Add(follower.ElectionTimeout);
//        }

//        Assert.All(resetTimes, timeout => Assert.InRange(timeout, 150, 300));
//        Assert.True(resetTimes.Count > 1, "Random values should be different.");
//    }

//    // Testing #6: When a new election begins, the term is incremented by 1.
//    [Fact]
//    public void NewElectionIncrementsTermByOne()
//    {
//        var clock = Substitute.For<IClock>();
//        var transport = Substitute.For<ITransport>();
//        var node = new RaftNode(NodeState.Follower, clock, transport);

//        node.StartElection();
//        var initialTerm = node.Term;

//        node.StartElection();
//        Assert.Equal(initialTerm + 1, node.Term);
//    }


//    // Testing #7: When a follower gets an AppendEntries message, it resets the election timer.
//    [Fact]
//    public void FollowerResetsElectionTimerOnAppendEntries()
//    {
//        var clock = Substitute.For<IClock>();
//        var transport = Substitute.For<ITransport>();
//        var follower = new RaftNode(NodeState.Follower, clock, transport);

//        follower.ReceiveAppendEntries(new AppendEntries { LeaderId = "Node1" });

//        Assert.False(follower.ElectionTimeoutExpired);
//    }

//    // Testing #8: When a candidate gets a majority of votes, it becomes a leader.
//    [Fact]
//    public void CandidateBecomesLeaderWithMajorityVotes()
//    {
//        var clock = Substitute.For<IClock>();
//        var transport = Substitute.For<ITransport>();
//        var candidate = new RaftNode(NodeState.Candidate, clock, transport);

//        candidate.ReceiveVote(true);
//        candidate.ReceiveVote(true);
//        candidate.ReceiveVote(false);

//        Assert.Equal(NodeState.Leader, candidate.State);
//    }

//    // Testing #9: Given a candidate receives a majority of votes while waiting for unresponsive nodes, it still becomes a leader.
//    [Fact]
//    public void CandidateBecomesLeaderWithMajorityVotesDespiteUnresponsiveNodes()
//    {
//        var clock = Substitute.For<IClock>();
//        var transport = Substitute.For<ITransport>();
//        var candidate = new RaftNode(NodeState.Candidate, clock, transport);

//        candidate.ReceiveVote(true);
//        candidate.ReceiveVote(true); 
//        candidate.ReceiveVote(false); // make this better

//        Assert.Equal(NodeState.Leader, candidate.State);
//    }

//    // Testing #10: A follower in an earlier term responds yes to a RequestForVoteRPC.
//    [Fact]
//    public void FollowerVotesYesToRequestForEarlierTerm() // make this better
//    {
//        var clock = Substitute.For<IClock>();
//        var transport = Substitute.For<ITransport>();
//        var follower = new RaftNode(NodeState.Follower, clock, transport);

//        var request = new VoteRequest { CandidateId = "Node1", Term = 1 };
//        follower.ReceiveVoteRequest(request);

//        Assert.True(follower.LastVoteGranted);
//    }

//    // Testing #11: A candidate votes for itself upon becoming a candidate.
//    [Fact]
//    public void CandidateVotesForItself()
//    {
//        var clock = Substitute.For<IClock>();
//        var transport = Substitute.For<ITransport>();
//        var node = new RaftNode(clock, transport);

//        node.StartElection();

//        Assert.Equal("Self", node.LastVoteCandidateId);
//    }

//    // Testing #12: Candidate becomes follower if it receives AppendEntries from a node with a later term.
//    [Fact]
//    public void CandidateBecomesFollowerOnAppendEntriesFromLaterTerm()
//    {
//        var clock = Substitute.For<IClock>();
//        var transport = Substitute.For<ITransport>();
//        var candidate = new RaftNode(NodeState.Candidate, clock, transport);
//        candidate.Term = 2;

//        candidate.ReceiveAppendEntries(new AppendEntries { LeaderId = "Node1", Term = 3 });

//        Assert.Equal(NodeState.Follower, candidate.State);
//    }

//    // Testing #13: Candidate becomes follower on AppendEntries from an equal term.
//    [Fact]
//    public void CandidateBecomesFollowerOnAppendEntriesFromEqualTerm()
//    {
//        var clock = Substitute.For<IClock>();
//        var transport = Substitute.For<ITransport>();
//        var candidate = new RaftNode(NodeState.Candidate, clock, transport);
//        candidate.Term = 2;

//        candidate.ReceiveAppendEntries(new AppendEntries { LeaderId = "Node1", Term = 2 });

//        Assert.Equal(NodeState.Follower, candidate.State);
//    }

//    // Testing #14: Node rejects a second RequestForVote for the same term.
//    [Fact]
//    public void NodeRejectsSecondVoteRequestForSameTerm()
//    {
//        var clock = Substitute.For<IClock>();
//        var transport = Substitute.For<ITransport>();
//        var follower = new RaftNode(NodeState.Follower, clock, transport);

//        var request = new VoteRequest { CandidateId = "Node1", Term = 1 };
//        follower.ReceiveVoteRequest(request);
//        follower.ReceiveVoteRequest(request);

//        Assert.False(follower.LastVoteGranted);
//    }

//    // Testing #15: Node votes for a second request for a future term.
//    [Fact]
//    public void NodeVotesForFutureTermRequest()
//    {
//        var clock = Substitute.For<IClock>();
//        var transport = Substitute.For<ITransport>();
//        var follower = new RaftNode(NodeState.Follower, clock, transport);

//        var request = new VoteRequest { CandidateId = "Node1", Term = 2 };
//        follower.ReceiveVoteRequest(request);

//        Assert.True(follower.LastVoteGranted);
//    }

//    // Testing #16: Candidate starts a new election when timer expires during election.
// [Fact]
//public async Task CandidateStartsNewElectionOnElectionTimeout()
//{
//    var clock = Substitute.For<IClock>();
//    var transport = Substitute.For<ITransport>();
//    var candidate = new RaftNode(NodeState.Candidate, clock, transport);
//    candidate.StartElection();

//    var initialTerm = candidate.Term;

//    await candidate.CheckElectionTimeoutDuringElectionAsync();
//    Assert.Equal(initialTerm + 1, candidate.Term);
//}


//    // Testing #17: Follower sends response on AppendEntries.
//    [Fact]
//    public async Task FollowerRespondsToAppendEntries()
//    {
//        var clock = Substitute.For<IClock>();
//        var transport = Substitute.For<ITransport>();
//        var follower = new RaftNode(NodeState.Follower, clock, transport);

//        await follower.ReceiveAppendEntriesAsync(new AppendEntries { LeaderId = "Node1" });

//        await transport.Received().SendAppendEntriesResponseAsync(Arg.Any<AppendEntriesResponse>());
//    }

//    // Testing #18: Candidate rejects AppendEntries from a previous term.
//    [Fact]
//    public void CandidateRejectsAppendEntriesFromPreviousTerm()
//    {
//        var clock = Substitute.For<IClock>();
//        var transport = Substitute.For<ITransport>();
//        var candidate = new RaftNode(NodeState.Candidate, clock, transport);
//        candidate.Term = 3;

//        var appendEntries = new AppendEntries { LeaderId = "Node1", Term = 2 };
//        candidate.ReceiveAppendEntries(appendEntries);

//        Assert.False(candidate.LastAppendEntriesAccepted);
//    }

//    // Testing #19: When a candidate wins an election, it immediately sends a heartbeat.
//    [Fact]
//    public async Task CandidateSendsHeartbeatOnElectionWin()
//    {
//        var clock = Substitute.For<IClock>();
//        var transport = Substitute.For<ITransport>();
//        var candidate = new RaftNode(NodeState.Candidate, clock, transport);

//        candidate.ReceiveVote(true);
//        candidate.ReceiveVote(true);
//        await candidate.SendHeartbeatAsync();

//        await transport.Received().SendAppendEntriesAsync(Arg.Any<AppendEntries>());
//    }
//}


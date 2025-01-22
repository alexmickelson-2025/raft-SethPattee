using Xunit;
using NSubstitute;
using System.Threading.Tasks;
using System.Collections.Generic;

public class RaftNodeLogTests
{
    [Fact]
    public async Task CandidateTransitionsToFollowerOnAppendEntriesFromHigherTermLeader()
    {
        // Arrange
        var clock = Substitute.For<IClock>();
        var transport = Substitute.For<ITransport>();
        var node = new RaftNode("Node1", NodeState.Candidate, clock, transport);
        node.Term = 5;

        var appendEntries = new AppendEntries
        {
            LeaderId = "Node2",
            Term = 6
        };

        // Act
        await node.ReceiveAppendEntriesAsync(appendEntries);

        // Assert
        Assert.Equal(NodeState.Follower, node.State);
        Assert.Equal(6, node.Term);
        Assert.Equal("Node2", node.CurrentLeaderId);
        Assert.True(node.LastAppendEntriesAccepted);
    }

    [Fact]
    public void FollowerRejectsVoteRequestIfAlreadyVotedInCurrentTerm()
    {
        // Arrange
        var clock = Substitute.For<IClock>();
        var transport = Substitute.For<ITransport>();
        var node = new RaftNode("Node1", clock, transport);

        node.State = NodeState.Follower;
        node.Term = 3; 
        node.LastVoteCandidateId = "Candidate1"; 

        var voteRequest = new VoteRequest
        {
            CandidateId = "Candidate2", 
            Term = 3 
        };

        // Act
        node.ReceiveVoteRequest(voteRequest);

        // Assert
        Assert.False(node.LastVoteGranted); 
        Assert.Equal("Candidate1", node.LastVoteCandidateId); 
        Assert.Equal(NodeState.Follower, node.State);
        Assert.Equal(3, node.Term);
    }

    [Fact]
    public async Task FollowerRejectsAppendEntriesWithOutdatedTerm()
    {
        // Arrange
        var clock = Substitute.For<IClock>();
        var transport = Substitute.For<ITransport>();
        var node = new RaftNode("Node1", clock, transport);

        node.State = NodeState.Follower;
        node.Term = 5; 

        var appendEntries = new AppendEntries
        {
            LeaderId = "Leader1",
            Term = 4 
        };

        // Act
        await node.ReceiveAppendEntriesAsync(appendEntries);

        // Assert
        Assert.False(node.LastAppendEntriesAccepted);
        Assert.Equal(5, node.Term);
        Assert.Null(node.CurrentLeaderId);
        Assert.Equal(NodeState.Follower, node.State); 
    }
}

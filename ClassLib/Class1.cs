﻿public interface IClock
{
    DateTime GetCurrentTime();
    void AdvanceBy(TimeSpan duration);
}

public interface ITransport
{
    Task SendAppendEntriesAsync(AppendEntries entries, string recipientNodeId);
    Task<bool> SendVoteRequestAsync(VoteRequest request, string recipientNodeId);
    Task SendAppendEntriesResponseAsync(AppendEntriesResponse response, string recipientNodeId);

    IEnumerable<string> GetOtherNodeIds(string currentNodeId); 
}

public class RaftNode
{
    public string NodeId { get; }
    public NodeState State { get; set; }
    public string? CurrentLeaderId { get; set; }
    public int Term { get; set; }
    public bool ElectionTimeoutExpired => _electionTimerExpired;
    public int ElectionTimeout { get;  set; }
    public string? LastVoteCandidateId { get; set; }
    public bool LastVoteGranted { get;  set; }
    public bool LastAppendEntriesAccepted { get;  set; }

    public IClock _clock;
    public ITransport _transport;
    public bool _electionTimerExpired;

    public RaftNode(string nodeId, IClock clock, ITransport transport)
    {
        NodeId = nodeId;
        State = NodeState.Follower;
        _clock = clock;
        _transport = transport;
        Term = 0;
        ResetElectionTimer();
    }

    public RaftNode(string nodeId, NodeState initialState, IClock clock, ITransport transport)
    {
        State = initialState;
        _clock = clock;
        _transport = transport;
        Term = 0;
        ResetElectionTimer();
    }

    public async Task SendHeartbeatAsync()
    {
        if (State == NodeState.Leader)
        {
            var tasks = _transport.GetOtherNodeIds(NodeId)
                                  .Select(id => _transport.SendAppendEntriesAsync(new AppendEntries
                                  {
                                      LeaderId = NodeId,
                                      Term = Term
                                  }, id));

            await Task.WhenAll(tasks); 
        }
    }


    public void ReceiveAppendEntries(AppendEntries appendEntries)
    {
        if (appendEntries.Term < Term)
        {
            LastAppendEntriesAccepted = false;
            return;
        }

        State = NodeState.Follower;
        CurrentLeaderId = appendEntries.LeaderId;
        Term = Math.Max(Term, appendEntries.Term);
        ResetElectionTimer();
        LastAppendEntriesAccepted = true;
    }

    public async Task ReceiveAppendEntriesAsync(AppendEntries appendEntries)
    {
        ReceiveAppendEntries(appendEntries);
        if (!string.IsNullOrEmpty(appendEntries.LeaderId))
        {
            await _transport.SendAppendEntriesResponseAsync(new AppendEntriesResponse
            {
                Success = LastAppendEntriesAccepted
            }, appendEntries.LeaderId); 
        }
    }


    public void ReceiveVoteRequest(VoteRequest request)
    {
        if (request.Term < Term || (request.Term == Term && LastVoteCandidateId != null))
        {
            LastVoteGranted = false;
            return;
        }

        LastVoteCandidateId = request.CandidateId;
        Term = Math.Max(Term, request.Term);
        LastVoteGranted = true;
    }

    public async Task StartElection()
    {
        if (State == NodeState.Follower || State == NodeState.Candidate)
        {
            State = NodeState.Candidate;
            Term++;
            LastVoteCandidateId = NodeId;

            
            var tasks = _transport.GetOtherNodeIds(NodeId)
                                  .Select(id => _transport.SendVoteRequestAsync(new VoteRequest
                                  {
                                      CandidateId = NodeId,
                                      Term = Term
                                  }, id));

            var responses = await Task.WhenAll(tasks);
            int votesGranted = responses.Count(v => v);

            if (votesGranted >= (_transport.GetOtherNodeIds(NodeId).Count() / 2) + 1)
            {
                State = NodeState.Leader;
                CurrentLeaderId = NodeId;
            }
        }
    }


    public void ReceiveVote(bool granted)
    {
        if (granted && State == NodeState.Candidate)
        {
            State = NodeState.Leader;
        }
    }

    public void ResetElectionTimer()
    {
        var random = new Random();
        ElectionTimeout = random.Next(150, 301);
        _electionTimerExpired = false;
    }

    public async Task CheckElectionTimeoutAsync()
    {
        await Task.Delay(ElectionTimeout);
        if (State == NodeState.Follower)
        {
            State = NodeState.Candidate;
            Term++;
            ResetElectionTimer();
        }
    }
    public async Task CheckElectionTimeoutDuringElectionAsync()
    {
        await Task.Delay(ElectionTimeout);
        if (State == NodeState.Candidate)
        {
            Term++;
            ResetElectionTimer();
        }
    }


}

public enum NodeState
{
    Follower,
    Candidate,
    Leader
}

public class AppendEntries
{
    public string LeaderId { get; set; } = string.Empty; 
    public int Term { get; set; } 
}

public class AppendEntriesResponse
{
    public bool Success { get; set; } 
}


public class VoteRequest
{
    public string CandidateId { get; set; } = "";
    public int Term { get; set; }
}

//RaftNode.cs
public interface IClock
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
    public async Task RunLeaderTasksAsync()
    {
        while (State == NodeState.Leader)
        {
            await SendHeartbeatAsync();
            await Task.Delay(50);
        }
    }

    public async Task SendHeartbeatAsync()
    {
        if (State == NodeState.Leader)
        {
            var otherNodes = _transport.GetOtherNodeIds(NodeId).ToList();
            var tasks = otherNodes.Select(id => _transport.SendAppendEntriesAsync(
                new AppendEntries
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

        ResetElectionTimer();
        _electionTimerExpired = false;

        if (State != NodeState.Follower)
        {
            State = NodeState.Follower;
            OnStateChanged();
        }

        CurrentLeaderId = appendEntries.LeaderId;
        Term = Math.Max(Term, appendEntries.Term);
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
        if (request.Term > Term)
        {
            Term = request.Term;
            State = NodeState.Follower;
            OnStateChanged();
            CurrentLeaderId = null;
            LastVoteCandidateId = null;
        }

        if (request.Term < Term ||
            (request.Term == Term && LastVoteCandidateId != null && LastVoteCandidateId != request.CandidateId))
        {
            LastVoteGranted = false;
            return;
        }

        LastVoteCandidateId = request.CandidateId;
        LastVoteGranted = true;
        ResetElectionTimer();
        _electionTimerExpired = false;
    }


    public async Task StartElection()
    {
        if (State == NodeState.Follower || State == NodeState.Candidate)
        {
            State = NodeState.Candidate;
            OnStateChanged();

            Term++;
            LastVoteCandidateId = NodeId;
            LastVoteGranted = true;

            int votesGranted = 1;

            try
            {
                var otherNodes = _transport.GetOtherNodeIds(NodeId).ToList();
                var tasks = otherNodes.Select(id => _transport.SendVoteRequestAsync(new VoteRequest
                {
                    CandidateId = NodeId,
                    Term = Term
                }, id));

                var responses = await Task.WhenAll(tasks);
                votesGranted += responses.Count(v => v);

                int majorityNeeded = (otherNodes.Count + 1) / 2 + 1;

                if (votesGranted >= majorityNeeded)
                {
                    State = NodeState.Leader;
                    CurrentLeaderId = NodeId;
                    OnStateChanged();
                    await SendHeartbeatAsync();
                }
                else
                {
                    State = NodeState.Follower;
                    OnStateChanged();
                    ResetElectionTimer();
                }
            }
            catch (Exception)
            {
                State = NodeState.Follower;
                OnStateChanged();
                ResetElectionTimer();
            }
        }
    }

    private void OnStateChanged()
    {
        Console.WriteLine($"Node {NodeId} changed state to {State} in Term {Term}");
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
        ElectionTimeout = random.Next(1500, 3001);
        _electionTimerExpired = false;
    }

    public async Task CheckElectionTimeoutAsync()
    {
        await Task.Delay(ElectionTimeout);

        if (State == NodeState.Follower && _electionTimerExpired)
        {
            await StartElection();
        }
    }

    public async Task CheckElectionTimeoutDuringElectionAsync()
    {
        if (State == NodeState.Candidate)
        {
            await Task.Delay(ElectionTimeout);
            if (State == NodeState.Candidate)
            {
                State = NodeState.Follower;
                OnStateChanged();
                ResetElectionTimer();
                _electionTimerExpired = false;
            }
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

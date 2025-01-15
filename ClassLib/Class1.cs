public interface IClock
{
    DateTime GetCurrentTime();
    void AdvanceBy(TimeSpan duration);
}

public interface ITransport
{
    Task SendAppendEntriesAsync(AppendEntries entries);
    Task<bool> SendVoteRequestAsync(VoteRequest request);
    Task SendAppendEntriesResponseAsync(AppendEntriesResponse response);
}

public class RaftNode
{
    public NodeState State { get; private set; }
    public string? CurrentLeaderId { get; private set; }
    public int Term { get; set; }
    public bool ElectionTimeoutExpired => _electionTimerExpired;
    public int ElectionTimeout { get; private set; }
    public string? LastVoteCandidateId { get; private set; }
    public bool LastVoteGranted { get; private set; }
    public bool LastAppendEntriesAccepted { get; private set; }

    private readonly IClock _clock;
    private readonly ITransport _transport;
    private bool _electionTimerExpired;

    public RaftNode(IClock clock, ITransport transport)
    {
        State = NodeState.Follower;
        _clock = clock;
        _transport = transport;
        Term = 0;
        ResetElectionTimer();
    }

    public RaftNode(NodeState initialState, IClock clock, ITransport transport)
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
            await _transport.SendAppendEntriesAsync(new AppendEntries());
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
        await _transport.SendAppendEntriesResponseAsync(new AppendEntriesResponse { Success = LastAppendEntriesAccepted });
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

    public void StartElection()
    {
        if (State == NodeState.Follower || State == NodeState.Candidate)
        {
            State = NodeState.Candidate;
            Term++;
            LastVoteCandidateId = "Self";
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
    public string LeaderId { get; set; } = "";
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

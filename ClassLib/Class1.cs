//RaftNode.cs
using NSubstitute.Routing.Handlers;

public interface IClock
{
    DateTime GetCurrentTime();
    void AdvanceBy(TimeSpan duration);
}

public interface ITransport
{
    Task<AppendEntriesResponse> SendAppendEntriesAsync(AppendEntries entries, string recipientNodeId);
    Task<bool> SendVoteRequestAsync(VoteRequest request, string recipientNodeId);
    Task SendAppendEntriesResponseAsync(AppendEntriesResponse response, string recipientNodeId);

    IEnumerable<string> GetOtherNodeIds(string currentNodeId); 
}

public interface IStateMachine
{
    Task ApplyAsync(string command);
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

    public List<LogEntry> _log = new List<LogEntry>();
    public Dictionary<string, int> _nextIndex = new Dictionary<string, int>();
    public Dictionary<string, int> NextIndex => _nextIndex;

    private readonly IStateMachine _stateMachine;
    private int _lastAppliedIndex = 0;

    private int _commitIndex = 0;
    public int CommitIndex
    {
        get => _commitIndex;
        set => _commitIndex = value;
    }
    public int TimerLowerBound { get; set; } = 1500;
    public int TimerUpperBound { get; set; } = 3001;
    public bool Paused { get; set; } = false; 


    public RaftNode(string nodeId, IClock clock, ITransport transport)
    {
        NodeId = nodeId;
        State = NodeState.Follower;
        _clock = clock;
        _transport = transport;
        Term = 0;
        ResetElectionTimer();
    }

    public RaftNode(string nodeId, NodeState initialState, IClock clock, ITransport transport, IStateMachine stateMachine)
    {
        NodeId = nodeId;
        State = initialState;
        _clock = clock;
        _transport = transport;
        _stateMachine = stateMachine;
        Term = 0;
        ResetElectionTimer();

        if (initialState == NodeState.Leader)
        {
            var otherNodes = _transport.GetOtherNodeIds(NodeId).ToList();
            foreach (var id in otherNodes)
            {
                _nextIndex[id] = _log.Count;
            }
        }
    }

    public async Task ReceiveClientCommandAsync(string command)
    {
        if (Paused == false)
        {
            if (State == NodeState.Leader)
            {
                var logEntry = new LogEntry { Term = Term, Command = command };
                _log.Add(logEntry);

                var otherNodes = _transport.GetOtherNodeIds(NodeId).ToList();
                foreach (var id in otherNodes)
                {
                    _nextIndex[id] = _log.Count + 1;
                }

                var tasks = otherNodes.Select(id => _transport.SendAppendEntriesAsync(
                    new AppendEntries
                    {
                        LeaderId = NodeId,
                        Term = Term,
                        LogEntries = _log,
                        LeaderCommit = _commitIndex
                    }, id));

                var responses = await Task.WhenAll(tasks);

                int successfulResponses = responses.Count(r => r != null && r.Success) + 1;

                int majorityNeeded = (otherNodes.Count + 1) / 2 + 1;

                if (successfulResponses >= majorityNeeded)
                {
                    _commitIndex = _log.Count;

                    await ApplyCommittedEntriesAsync();
                }
            }
        }
    }
    

    public List<LogEntry> GetLog()
    {

        return _log;
    }
    public async Task RunLeaderTasksAsync()
    {
        if (Paused == false)
        {
            while (State == NodeState.Leader)
            {
                if (Paused == true)
                { return; }
                else
                {
                    await SendHeartbeatAsync();
                    await Task.Delay(50);
                }
            }
        }
    }
    private async Task ApplyCommittedEntriesAsync()
    {
        if (Paused == false)
        {
            while (_lastAppliedIndex < _commitIndex)
            {
                _lastAppliedIndex++;
                var logEntry = _log[_lastAppliedIndex - 1];
                await _stateMachine.ApplyAsync(logEntry.Command);
            }
        }
    }

    
    public async Task ReceiveClientCommandAsync(string command, Action<bool> onCommitConfirmed = null)
    {
        if (Paused == false)
        {
            if (State == NodeState.Leader)
            {
                var logEntry = new LogEntry { Term = Term, Command = command };
                _log.Add(logEntry);

                foreach (var id in _nextIndex.Keys.ToList())
                {
                    _nextIndex[id] = _log.Count + 1;
                }

                await SendHeartbeatAsync();

                int majorityNeeded = (_nextIndex.Count + 1) / 2 + 1;
                int responsesReceived = 1;

                foreach (var id in _nextIndex.Keys)
                {
                    var response = await _transport.SendAppendEntriesAsync(
                        new AppendEntries
                        {
                            LeaderId = NodeId,
                            Term = Term,
                            LogEntries = _log,
                            LeaderCommit = _commitIndex
                        }, id);

                    if (response.Success)
                    {
                        responsesReceived++;
                    }

                    Console.WriteLine($"Responses received: {responsesReceived}, Majority needed: {majorityNeeded}");

                    if (responsesReceived >= majorityNeeded)
                    {
                        _commitIndex = _log.Count;
                        Console.WriteLine($"CommitIndex updated to: {_commitIndex}");
                        await ApplyCommittedEntriesAsync();

                        onCommitConfirmed?.Invoke(true);
                        break;
                    }
                }
            }
        }
    }

    public async Task SendHeartbeatAsync()
    {
        if (Paused == false)
        {
            if (State == NodeState.Leader)
            {
                var otherNodes = _transport.GetOtherNodeIds(NodeId).ToList();

                foreach (var id in otherNodes)
                {
                    if (!_nextIndex.ContainsKey(id))
                    {
                        throw new InvalidOperationException("Leader Does not have index for follower nodes");
                        //_nextIndex[id] = _log.Count + 1;
                    }

                    int prevLogIndex = _nextIndex[id] - 1;
                    int prevLogTerm = prevLogIndex >= 0 && prevLogIndex < _log.Count ? _log[prevLogIndex].Term : -1;

                    await _transport.SendAppendEntriesAsync(
                        new AppendEntries
                        {
                            LeaderId = NodeId,
                            Term = Term,
                            PrevLogIndex = prevLogIndex,
                            PrevLogTerm = prevLogTerm,
                            LeaderCommit = _commitIndex,
                            LogEntries = new List<LogEntry>()
                        }, id);
                }
            }
        }
    }


    public void ReceiveAppendEntries(AppendEntries appendEntries)
    {
        if (Paused == false)
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

            if (appendEntries.LogEntries != null && appendEntries.LogEntries.Count > 0)
            {
                _log.AddRange(appendEntries.LogEntries);
            }

            if (appendEntries.LeaderCommit > _commitIndex)
            {
                _commitIndex = Math.Min(appendEntries.LeaderCommit, _log.Count);
                _ = ApplyCommittedEntriesAsync();
            }
        }
    }

    public async Task ReceiveAppendEntriesAsync(AppendEntries appendEntries)
    {
        if (Paused == false)
        {
            if (appendEntries.Term < Term)
            {
                LastAppendEntriesAccepted = false;
                await SendAppendEntriesResponseAsync(false);
                return;
            }

            ResetElectionTimer();
            _electionTimerExpired = false;

            if (appendEntries.Term > Term)
            {
                Term = appendEntries.Term;
                State = NodeState.Follower;
                OnStateChanged();
            }

            CurrentLeaderId = appendEntries.LeaderId;

            if (appendEntries.PrevLogIndex >= 0)
            {
                if (appendEntries.PrevLogIndex >= _log.Count)
                {
                    LastAppendEntriesAccepted = false;
                    await SendAppendEntriesResponseAsync(false);
                    return;
                }

                if (_log[appendEntries.PrevLogIndex].Term != appendEntries.PrevLogTerm)
                {
                    LastAppendEntriesAccepted = false;
                    await SendAppendEntriesResponseAsync(false);
                    return;
                }
            }

            LastAppendEntriesAccepted = true;

            if (appendEntries.LogEntries != null && appendEntries.LogEntries.Count > 0)
            {
                _log.AddRange(appendEntries.LogEntries);
            }

            if (appendEntries.LeaderCommit > _commitIndex)
            {
                _commitIndex = Math.Min(appendEntries.LeaderCommit, _log.Count - 1);
            }

            await ApplyCommittedEntriesAsync();

            await SendAppendEntriesResponseAsync(true);
        }
    }

    private async Task SendAppendEntriesResponseAsync(bool success)
    {
        if (Paused == false)
        {
            if (_transport == null) return;

            int lastLogIndex = _log?.Count > 0 ? _log.Count - 1 : -1;

            var response = new AppendEntriesResponse
            {
                Success = success,
                Term = Term,
                LastLogIndex = lastLogIndex
            };

            Console.WriteLine($"Sending response: Success={response.Success}, Term={response.Term}");

            await _transport.SendAppendEntriesResponseAsync(response, CurrentLeaderId ?? string.Empty);
        }
    }



    public async Task ReceiveAppendEntriesResponseAsync(AppendEntriesResponse response, string followerId)
    {
        if (Paused == false)
        {
            if (State != NodeState.Leader)
            {
                return;
            }

            if (response.Success)
            {
                _nextIndex[followerId] = _log.Count + 1;

                int replicatedCount = _nextIndex.Count(kvp => kvp.Value > _commitIndex);
                int majorityNeeded = (_nextIndex.Count + 1) / 2 + 1;

                if (replicatedCount >= majorityNeeded)
                {
                    _commitIndex = _log.Count;

                    await ApplyCommittedEntriesAsync();
                }
            }
            else
            {
                _nextIndex[followerId] = Math.Max(_nextIndex[followerId] - 1, 1);

                int prevLogIndex = _nextIndex[followerId] - 1;
                int prevLogTerm = prevLogIndex >= 0 && prevLogIndex < _log.Count ? _log[prevLogIndex].Term : -1;

                await _transport.SendAppendEntriesAsync(
                    new AppendEntries
                    {
                        LeaderId = NodeId,
                        Term = Term,
                        PrevLogIndex = prevLogIndex,
                        PrevLogTerm = prevLogTerm,
                        LeaderCommit = _commitIndex,
                        LogEntries = _log.Skip(prevLogIndex + 1).ToList()
                    }, followerId);
            }
        }
    }





    public void ReceiveVoteRequest(VoteRequest request)
    {
        if (Paused == false)
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
    }


    public async Task StartElection()
    {
        if (Paused == false)
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

                        foreach (var id in otherNodes)
                        {
                            _nextIndex[id] = _log.Count + 1;
                        }

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
    }

    public void OnStateChanged()
    {
        Console.WriteLine($"Node {NodeId} changed state to {State} in Term {Term}");
    }



    public void ReceiveVote(bool granted)
    {
        if (Paused == false)
        {
            if (granted && State == NodeState.Candidate)
            {
                State = NodeState.Leader;
            }
        }
    }

    public void ResetElectionTimer()
    {
        if (Paused == false)
        {
            var random = new Random();
            ElectionTimeout = random.Next(TimerLowerBound, TimerUpperBound);
            _electionTimerExpired = false;
        }
    }

    public async Task CheckElectionTimeoutAsync()
    {
        if (Paused == false)
        {
            await Task.Delay(ElectionTimeout);

            if (State == NodeState.Follower && _electionTimerExpired)
            {
                await StartElection();
            }
        }
    }

    public async Task CheckElectionTimeoutDuringElectionAsync()
    {
        if (Paused == false)
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


}

public enum NodeState
{
    Follower,
    Candidate,
    Leader
}

public class LogEntry
{
    public int Term { get; set; }
    public string Command { get; set; } = string.Empty;
}

public class AppendEntries
{
    public string LeaderId { get; set; } = string.Empty;
    public int Term { get; set; }
    public int PrevLogIndex { get; set; } = -1;
    public int PrevLogTerm { get; set; } = -1;
    public int LeaderCommit { get; set; }
    public List<LogEntry> LogEntries { get; set; } = new List<LogEntry>();
}


public class AppendEntriesResponse
{
    public bool Success { get; set; }
    public int Term { get; set; }
    public int LastLogIndex { get; set; }
}


public class VoteRequest
{
    public string CandidateId { get; set; } = "";
    public int Term { get; set; }
}

public class SimpleStateMachine : IStateMachine
{
    public List<string> AppliedCommands { get; } = new List<string>();

    public Task ApplyAsync(string command)
    {
        AppliedCommands.Add(command);
        Console.WriteLine($"Applied command: {command}");
        return Task.CompletedTask;
    }
}

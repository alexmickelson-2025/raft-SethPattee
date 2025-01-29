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
    event Action<string> OnCommandApplied;
    Dictionary<string, string> GetState();
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

    public event Action<string> OnStateMachineUpdate;
    public IReadOnlyList<LogEntry> Log => _log.AsReadOnly();
    public int CommitIndex
    {
        get => _commitIndex;
        set => _commitIndex = value;
    }
    public int TimerLowerBound { get; set; } = 1500;
    public int TimerUpperBound { get; set; } = 3001;
    public bool Paused { get; set; } = false;

    private const int HeartbeatInterval = 500;


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

    public RaftNode(string nodeId, NodeState initialState, IClock clock, ITransport transport, SimpleStateMachine stateMachine)
    {
        NodeId = nodeId;
        State = initialState;
        _clock = clock;
        _transport = transport;
        _stateMachine = stateMachine;
        Term = 0;
        ResetElectionTimer();

        _stateMachine.OnCommandApplied += (command) => OnStateMachineUpdate?.Invoke(command);

        if (initialState == NodeState.Leader)
        {
            var otherNodes = _transport.GetOtherNodeIds(NodeId).ToList();
            foreach (var id in otherNodes)
            {
                _nextIndex[id] = _log.Count;
            }
        }
    }

    public bool IsEntryCommitted(int logIndex)
    {
        return logIndex <= _commitIndex;
    }
    public async Task<bool> ReceiveClientCommandAsync(string command, Action<bool> onCommitConfirmed = null)
    {
        if (State != NodeState.Leader || Paused)
        {
            Console.WriteLine("Node is not the leader or is paused.");
            onCommitConfirmed?.Invoke(false);
            return false;
        }

        try
        {
            var logEntry = new LogEntry { Term = Term, Command = command };
            _log.Add(logEntry);
            int newEntryIndex = _log.Count - 1;

            var replicationSuccess = new Dictionary<string, bool>();
            var otherNodes = _transport.GetOtherNodeIds(NodeId).ToList();

            replicationSuccess[NodeId] = true;
            int majorityNeeded = (otherNodes.Count + 1) / 2 + 1;

            foreach (var followerId in otherNodes)
            {
                try
                {
                    var prevLogIndex = _nextIndex[followerId] - 1;
                    var prevLogTerm = prevLogIndex >= 0 && prevLogIndex < _log.Count
                        ? _log[prevLogIndex].Term
                        : -1;

                    var entries = _log.Skip(_nextIndex[followerId]).ToList();

                    var appendEntries = new AppendEntries
                    {
                        Term = Term,
                        LeaderId = NodeId,
                        PrevLogIndex = prevLogIndex,
                        PrevLogTerm = prevLogTerm,
                        LeaderCommit = _commitIndex,
                        LogEntries = entries
                    };

                    var response = await _transport.SendAppendEntriesAsync(appendEntries, followerId);

                    if (response.Success)
                    {
                        _nextIndex[followerId] = newEntryIndex + 1;
                        replicationSuccess[followerId] = true;

                        if (replicationSuccess.Count(x => x.Value) >= majorityNeeded)
                        {
                            _commitIndex = newEntryIndex;
                            await ApplyCommittedEntriesAsync();
                            onCommitConfirmed?.Invoke(true);
                            Console.WriteLine("Command committed successfully.");
                            return true;
                        }
                    }
                    else
                    {
                        _nextIndex[followerId] = Math.Max(0, _nextIndex[followerId] - 1);
                        Console.WriteLine($"Replication failed for follower {followerId}.");
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Error replicating log to {followerId}: {ex.Message}");
                    continue;
                }
            }

            _log.RemoveAt(_log.Count - 1);
            onCommitConfirmed?.Invoke(false);
            Console.WriteLine("Command failed to commit.");
            return false;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error in ReceiveClientCommandAsync: {ex.Message}");
            onCommitConfirmed?.Invoke(false);
            return false;
        }
    }

    public Dictionary<string, string> GetStateMachineState()
    {
        return (_stateMachine as SimpleStateMachine)?.GetState() ?? new Dictionary<string, string>();
    }

    private async Task ApplyCommittedEntriesAsync()
    {
        while (_lastAppliedIndex < _commitIndex)
        {
            _lastAppliedIndex++;
            if (_lastAppliedIndex < _log.Count)
            {
                var entry = _log[_lastAppliedIndex];
                try
                {
                    await _stateMachine.ApplyAsync(entry.Command);
                    Console.WriteLine($"Applied command to state machine: {entry.Command}");
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Error applying command to state machine: {ex.Message}");
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
                    await Task.Delay(HeartbeatInterval);
                }
            }
        }
    }
    //private async Task ApplyCommittedEntriesAsync()
    //{
    //    if (Paused == false)
    //    {
    //        while (_lastAppliedIndex < _commitIndex)
    //        {
    //            _lastAppliedIndex++;
    //            var logEntry = _log[_lastAppliedIndex - 1];
    //            await _stateMachine.ApplyAsync(logEntry.Command);
    //        }
    //    }
    //}


    //public async Task ReceiveClientCommandAsync(string command, Action<bool> onCommitConfirmed = null)
    //{
    //    if (Paused == false)
    //    {
    //        if (State == NodeState.Leader)
    //        {
    //            var logEntry = new LogEntry { Term = Term, Command = command };
    //            _log.Add(logEntry);

    //            foreach (var id in _nextIndex.Keys.ToList())
    //            {
    //                _nextIndex[id] = _log.Count + 1;
    //            }

    //            await SendHeartbeatAsync();

    //            int majorityNeeded = (_nextIndex.Count + 1) / 2 + 1;
    //            int responsesReceived = 1;

    //            foreach (var id in _nextIndex.Keys)
    //            {



    //                var response = await _transport.SendAppendEntriesAsync(
    //                    new AppendEntries
    //                    {
    //                        LeaderId = NodeId,
    //                        Term = Term,
    //                        LogEntries = _log,
    //                        LeaderCommit = _commitIndex
    //                    }, id);

    //                if (response.Success)
    //                {
    //                    responsesReceived++;
    //                }

    //                Console.WriteLine($"Responses received: {responsesReceived}, Majority needed: {majorityNeeded}");

    //                if (responsesReceived >= majorityNeeded)
    //                {
    //                    _commitIndex = _log.Count;
    //                    Console.WriteLine($"CommitIndex updated to: {_commitIndex}");
    //                    await ApplyCommittedEntriesAsync();

    //                    onCommitConfirmed?.Invoke(true);
    //                    break;
    //                }
    //            }
    //        }
    //    }
    //}

    public async Task SendHeartbeatAsync()
    {
        if (State != NodeState.Leader || Paused)
            return;

        var otherNodes = _transport.GetOtherNodeIds(NodeId).ToList();

        foreach (var followerId in otherNodes)
        {
            try
            {
                if (!_nextIndex.ContainsKey(followerId))
                {
                    _nextIndex[followerId] = _log.Count;
                    continue;
                }

                var prevLogIndex = _nextIndex[followerId] - 1;
                var prevLogTerm = prevLogIndex >= 0 && prevLogIndex < _log.Count
                    ? _log[prevLogIndex].Term
                    : -1;

                var entriesToSend = _log
                    .Skip(_nextIndex[followerId])
                    .ToList();

                var appendEntries = new AppendEntries
                {
                    Term = Term,
                    LeaderId = NodeId,
                    PrevLogIndex = prevLogIndex,
                    PrevLogTerm = prevLogTerm,
                    LeaderCommit = _commitIndex,
                    LogEntries = entriesToSend
                };

                var response = await _transport.SendAppendEntriesAsync(appendEntries, followerId);

                if (response.Success)
                {
                    _nextIndex[followerId] = _log.Count;
                }
                else if (response.Term > Term)
                {
                    Term = response.Term;
                    State = NodeState.Follower;
                    CurrentLeaderId = null;
                    return;
                }
                else
                {
                    _nextIndex[followerId] = Math.Max(0, _nextIndex[followerId] - 1);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error sending heartbeat to {followerId}: {ex.Message}");
                continue;
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
        if (Paused) return;
        if (appendEntries.Term > Term)
        {
            Term = appendEntries.Term;
            State = NodeState.Follower;
            CurrentLeaderId = appendEntries.LeaderId;
            LastVoteCandidateId = null;
            OnStateChanged();
        }

        if (appendEntries.Term < Term)
        {
            await SendAppendEntriesResponseAsync(false);
            return;
        }

        ResetElectionTimer();
        _electionTimerExpired = false;
        CurrentLeaderId = appendEntries.LeaderId;

        if (appendEntries.PrevLogIndex >= 0)
        {
            if (appendEntries.PrevLogIndex >= _log.Count)
            {
                await SendAppendEntriesResponseAsync(false);
                return;
            }

            if (_log[appendEntries.PrevLogIndex].Term != appendEntries.PrevLogTerm)
            {
                _log.RemoveRange(appendEntries.PrevLogIndex, _log.Count - appendEntries.PrevLogIndex);
                await SendAppendEntriesResponseAsync(false);
                return;
            }
        }

        if (appendEntries.LogEntries != null && appendEntries.LogEntries.Count > 0)
        {
            int newEntriesIndex = 0;
            int logIndex = appendEntries.PrevLogIndex + 1;

            while (logIndex < _log.Count &&
                   newEntriesIndex < appendEntries.LogEntries.Count)
            {
                if (_log[logIndex].Term != appendEntries.LogEntries[newEntriesIndex].Term)
                {
                    break;
                }
                logIndex++;
                newEntriesIndex++;
            }

            if (logIndex < _log.Count)
            {
                _log.RemoveRange(logIndex, _log.Count - logIndex);
            }

            while (newEntriesIndex < appendEntries.LogEntries.Count)
            {
                _log.Add(appendEntries.LogEntries[newEntriesIndex]);
                newEntriesIndex++;
            }
        }

        if (appendEntries.LeaderCommit > _commitIndex)
        {
            _commitIndex = Math.Min(appendEntries.LeaderCommit, _log.Count - 1);
            await ApplyCommittedEntriesAsync();
        }

        await SendAppendEntriesResponseAsync(true);
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
        if (Paused) return;
        if (State == NodeState.Leader) return;

        State = NodeState.Candidate;
        OnStateChanged();

        Term++;
        LastVoteCandidateId = NodeId;
        LastVoteGranted = true;
        CurrentLeaderId = null;

        ResetElectionTimer();

        int votesReceived = 1;
        var otherNodes = _transport.GetOtherNodeIds(NodeId).ToList();
        int majorityNeeded = (otherNodes.Count + 1) / 2 + 1;

        try
        {
            var voteRequest = new VoteRequest
            {
                CandidateId = NodeId,
                Term = Term,
                LastLogIndex = _log.Count - 1,
                LastLogTerm = _log.Count > 0 ? _log[^1].Term : 0
            };

            var voteTasks = otherNodes.Select(async nodeId =>
            {
                try
                {
                    return await _transport.SendVoteRequestAsync(voteRequest, nodeId);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Error sending vote request to {nodeId}: {ex.Message}");
                    return false;
                }
            }).ToList();

            while (voteTasks.Any())
            {
                var completedTask = await Task.WhenAny(voteTasks);
                voteTasks.Remove(completedTask);

                if (await completedTask)
                {
                    votesReceived++;
                }

                if (votesReceived >= majorityNeeded)
                {
                    if (State == NodeState.Candidate)
                    {
                        State = NodeState.Leader;
                        CurrentLeaderId = NodeId;
                        OnStateChanged();

                        foreach (var id in otherNodes)
                        {
                            _nextIndex[id] = _log.Count;
                        }

                        await SendHeartbeatAsync();
                    }
                    return;
                }
            }

            if (State == NodeState.Candidate)
            {
                State = NodeState.Follower;
                OnStateChanged();
                ResetElectionTimer();
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error in StartElection: {ex.Message}");
            if (State == NodeState.Candidate)
            {
                State = NodeState.Follower;
                OnStateChanged();
                ResetElectionTimer();
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
    public int LastLogIndex { get; set; }
    public int LastLogTerm { get; set; }
}

public class SimpleStateMachine : IStateMachine
{
    private Dictionary<string, string> _state = new();
    public event Action<string>? OnCommandApplied;

    public async Task ApplyAsync(string command)
    {
        if (string.IsNullOrWhiteSpace(command))
            return;

        var parts = command.Split(' ', 3);
        if (parts.Length >= 3 && parts[0].Equals("SET", StringComparison.OrdinalIgnoreCase))
        {
            var key = parts[1];
            var value = parts[2];
            _state[key] = value;

            OnCommandApplied?.Invoke($"SET {key}={value}");
            Console.WriteLine($"State machine updated: {key}={value}");
        }
    }

    public Dictionary<string, string> GetState()
    {
        return new Dictionary<string, string>(_state);
    }
}

public class CommandResult
{
    public bool Success { get; set; }
    public string Message { get; set; }
    public Dictionary<string, string> StateMachineState { get; set; }
}

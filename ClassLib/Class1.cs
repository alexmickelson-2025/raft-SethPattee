//RaftNode.cs

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

    private const int HeartbeatInterval = 150;

    public event Action<string, bool, string> OnCommandResponse;


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
        if (Paused)
        {
            OnCommandResponse?.Invoke(NodeId, false, "Node is paused");
            onCommitConfirmed?.Invoke(false);
            return false;
        }

        if (State != NodeState.Leader)
        {
            if (string.IsNullOrEmpty(CurrentLeaderId))
            {
                OnCommandResponse?.Invoke(NodeId, false, "No leader currently known");
                onCommitConfirmed?.Invoke(false);
                return false;
            }

            try
            {
                var appendEntries = new AppendEntries
                {
                    Term = Term,
                    LeaderId = CurrentLeaderId,
                    LogEntries = new List<LogEntry> { new LogEntry { Term = Term, Command = command } }
                };

                var response = await _transport.SendAppendEntriesAsync(appendEntries, CurrentLeaderId);

                if (response.Success)
                {
                    OnCommandResponse?.Invoke(NodeId, true, $"Command forwarded to leader {CurrentLeaderId}");
                    onCommitConfirmed?.Invoke(true);
                    return true;
                }
                else
                {
                    OnCommandResponse?.Invoke(NodeId, false, $"Leader {CurrentLeaderId} rejected the command");
                    onCommitConfirmed?.Invoke(false);
                    return false;
                }
            }
            catch (Exception ex)
            {
                OnCommandResponse?.Invoke(NodeId, false, $"Error forwarding command: {ex.Message}");
                onCommitConfirmed?.Invoke(false);
                return false;
            }
        }

        try
        {
            var logEntry = new LogEntry { Term = Term, Command = command };
            _log.Add(logEntry);
            int newEntryIndex = _log.Count - 1;

            Console.WriteLine($"Leader {NodeId}: Added new entry at index {newEntryIndex}");

            var replicationSuccess = new Dictionary<string, bool>();
            var otherNodes = _transport.GetOtherNodeIds(NodeId).ToList();
            int majorityNeeded = (otherNodes.Count + 1) / 2 + 1;

            bool commitSuccessful = await TryReplicateEntry(newEntryIndex, replicationSuccess, otherNodes, majorityNeeded);

            if (commitSuccessful)
            {
                OnCommandResponse?.Invoke(NodeId, true, "Command committed successfully");
                onCommitConfirmed?.Invoke(true);
                return true;
            }
            else if (State == NodeState.Leader)
            {
                _log.RemoveAt(_log.Count - 1);
                OnCommandResponse?.Invoke(NodeId, false, "Failed to replicate command");
                onCommitConfirmed?.Invoke(false);
            }

            return false;
        }
        catch (Exception ex)
        {
            OnCommandResponse?.Invoke(NodeId, false, $"Error processing command: {ex.Message}");
            onCommitConfirmed?.Invoke(false);
            return false;
        }
    }
    private async Task<bool> TryReplicateEntry(int newEntryIndex, Dictionary<string, bool> replicationSuccess,
    List<string> otherNodes, int majorityNeeded)
    {
        int maxRetries = 3;

        Console.WriteLine($"Leader {NodeId}: Attempting to replicate entry {newEntryIndex}. Majority needed: {majorityNeeded}");

        for (int retry = 0; retry < maxRetries && State == NodeState.Leader && !Paused; retry++)
        {
            foreach (var followerId in otherNodes)
            {
                if (replicationSuccess.ContainsKey(followerId) && replicationSuccess[followerId])
                    continue;

                try
                {
                    var prevLogIndex = _nextIndex[followerId] - 1;
                    var prevLogTerm = prevLogIndex >= 0 && prevLogIndex < _log.Count
                        ? _log[prevLogIndex].Term
                        : -1;

                    var entriesToSend = _log
                        .Skip(_nextIndex[followerId])
                        .ToList();

                    Console.WriteLine($"Leader {NodeId}: Sending to {followerId} - PrevLogIndex: {prevLogIndex}, Entries: {entriesToSend.Count}");

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
                        _nextIndex[followerId] = newEntryIndex + 1;
                        replicationSuccess[followerId] = true;
                        Console.WriteLine($"Leader {NodeId}: Successful replication to {followerId}");

                        Console.WriteLine($"Leader {NodeId}: Current replication status:");
                        foreach (var status in replicationSuccess)
                        {
                            Console.WriteLine($"  Node {status.Key}: {status.Value}");
                        }

                        int successCount = replicationSuccess.Count(x => x.Value) + 1; 
                        Console.WriteLine($"Leader {NodeId}: Success count: {successCount}, Majority needed: {majorityNeeded}");

                        if (successCount >= majorityNeeded)
                        {
                            _commitIndex = newEntryIndex;
                            Console.WriteLine($"Leader {NodeId}: Majority achieved. Committing entry {newEntryIndex}");
                            await ApplyCommittedEntriesAsync();
                            return true;
                        }
                    }
                    else if (response.Term > Term)
                    {
                        Console.WriteLine($"Leader {NodeId}: Stepping down (higher term: {response.Term})");
                        Term = response.Term;
                        State = NodeState.Follower;
                        CurrentLeaderId = null;
                        return false;
                    }
                    else
                    {
                        _nextIndex[followerId] = Math.Max(0, _nextIndex[followerId] - 1);
                        Console.WriteLine($"Leader {NodeId}: Failed replication to {followerId}, decremented nextIndex to {_nextIndex[followerId]}");
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Error replicating to {followerId}: {ex.Message}");
                }
            }

            if (retry < maxRetries - 1)
                await Task.Delay(50);
        }

        return false;
    }


    public Dictionary<string, string> GetStateMachineState()
    {
        return (_stateMachine as SimpleStateMachine)?.GetState() ?? new Dictionary<string, string>();
    }

    private async Task ApplyCommittedEntriesAsync()
    {
        try
        {
            while (_lastAppliedIndex < _commitIndex)
            {
                _lastAppliedIndex++;
                if (_lastAppliedIndex < _log.Count)
                {
                    var entry = _log[_lastAppliedIndex];
                    await _stateMachine.ApplyAsync(entry.Command);
                    Console.WriteLine($"Node {NodeId}: Applied command at index {_lastAppliedIndex}: {entry.Command}");
                }
                else
                {
                    Console.WriteLine($"Node {NodeId}: Warning - Commit index ahead of log entries");
                    break;
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error applying committed entries: {ex.Message}");
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

    public async Task SendHeartbeatAsync()
    {
        if (State != NodeState.Leader || Paused)
            return;

        var otherNodes = _transport.GetOtherNodeIds(NodeId).ToList();

        foreach (var followerId in otherNodes)
        {
            if (!_nextIndex.ContainsKey(followerId))
            {
                _nextIndex[followerId] = _log.Count;
                continue;
            }

            try
            {
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
        if (Paused)
        {
            Console.WriteLine($"Node {NodeId}: Paused, rejecting AppendEntries from {appendEntries.LeaderId}");
            await SendAppendEntriesResponseAsync(false);
            return;
        }

        if (appendEntries.Term < Term)
        {
            Console.WriteLine($"Node {NodeId}: Rejecting AppendEntries from {appendEntries.LeaderId} (lower term: {appendEntries.Term} < {Term})");
            await SendAppendEntriesResponseAsync(false);
            return;
        }

        if (appendEntries.Term > Term)
        {
            Console.WriteLine($"Node {NodeId}: Updating term to {appendEntries.Term} (was {Term})");
            Term = appendEntries.Term;
            State = NodeState.Follower;
            CurrentLeaderId = appendEntries.LeaderId;
            LastVoteCandidateId = null;
        }

        ResetElectionTimer();
        _electionTimerExpired = false;
        CurrentLeaderId = appendEntries.LeaderId;

        if (appendEntries.PrevLogIndex >= 0)
        {
            bool logOk = appendEntries.PrevLogIndex < _log.Count &&
                         (appendEntries.PrevLogIndex == -1 ||
                          _log[appendEntries.PrevLogIndex].Term == appendEntries.PrevLogTerm);

            if (!logOk)
            {
                Console.WriteLine($"Node {NodeId}: Log inconsistency at index {appendEntries.PrevLogIndex}");
                await SendAppendEntriesResponseAsync(false);
                return;
            }
        }

        if (appendEntries.LogEntries != null && appendEntries.LogEntries.Any())
        {
            int newEntriesStartIndex = appendEntries.PrevLogIndex + 1;

            if (newEntriesStartIndex < _log.Count)
            {
                _log.RemoveRange(newEntriesStartIndex, _log.Count - newEntriesStartIndex);
            }

            _log.AddRange(appendEntries.LogEntries);
            Console.WriteLine($"Node {NodeId}: Added {appendEntries.LogEntries.Count} entries starting at index {newEntriesStartIndex}");
        }

        if (appendEntries.LeaderCommit > _commitIndex)
        {
            int lastNewIndex = appendEntries.PrevLogIndex + appendEntries.LogEntries.Count;
            _commitIndex = Math.Min(appendEntries.LeaderCommit, lastNewIndex);
            Console.WriteLine($"Node {NodeId}: Updated commit index to {_commitIndex}");
            await ApplyCommittedEntriesAsync();
        }
        LastAppendEntriesAccepted = true;
        await SendAppendEntriesResponseAsync(true);
    }

    private async Task SendAppendEntriesResponseAsync(bool success)
    {
        if (Paused || _transport == null) return;

        try
        {
            var response = new AppendEntriesResponse
            {
                Success = success,
                Term = Term,
                LastLogIndex = _log.Count - 1
            };

            await _transport.SendAppendEntriesResponseAsync(response, CurrentLeaderId ?? string.Empty);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error sending AppendEntries response: {ex.Message}");
        }
    }



    public async Task ReceiveAppendEntriesResponseAsync(AppendEntriesResponse response, string followerId)
    {
        if (Paused || State != NodeState.Leader)
            return;

        if (response.Term > Term)
        {
            Term = response.Term;
            State = NodeState.Follower;
            CurrentLeaderId = null;
            return;
        }

        if (response.Success)
        {
            _nextIndex[followerId] = response.LastLogIndex + 1;

            for (int i = _commitIndex + 1; i < _log.Count; i++)
            {
                if (_log[i].Term == Term)
                {
                    int replicationCount = 1;
                    foreach (var node in _nextIndex)
                    {
                        if (node.Value > i)
                            replicationCount++;
                    }

                    if (replicationCount >= (_nextIndex.Count + 1) / 2 + 1)
                    {
                        _commitIndex = i;
                        await ApplyCommittedEntriesAsync();
                    }
                    else
                    {
                        break;
                    }
                }
            }
        }
        else
        {
            _nextIndex[followerId] = Math.Max(0, _nextIndex[followerId] - 1);

            int prevLogIndex = _nextIndex[followerId] - 1;
            int prevLogTerm = prevLogIndex >= 0 && prevLogIndex < _log.Count ? _log[prevLogIndex].Term : -1;

            var appendEntries = new AppendEntries
            {
                LeaderId = NodeId,
                Term = Term,
                PrevLogIndex = prevLogIndex,
                PrevLogTerm = prevLogTerm,
                LeaderCommit = _commitIndex,
                LogEntries = _log.Skip(_nextIndex[followerId]).ToList()
            };

            await _transport.SendAppendEntriesAsync(appendEntries, followerId);
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
            ElectionTimeout = random.Next(TimerLowerBound *2 , TimerUpperBound*2 );
            _electionTimerExpired = false;
        }
    }

    public async Task CheckElectionTimeoutAsync()
    {
        if (Paused)
            return;

        await Task.Delay(ElectionTimeout);

        if (State == NodeState.Follower && _electionTimerExpired)
        {
            Console.WriteLine($"Node {NodeId}: Election timeout expired. Starting new election.");
            await Task.Delay(new Random().Next(50, 150));
            await StartElection();
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

    public void PauseNode()
    {
        Paused = true;
        Console.WriteLine($"Node {NodeId} paused");
    }

    public void ResumeNode()
    {
        Paused = false;
        ResetElectionTimer();
        Console.WriteLine($"Node {NodeId} resumed");
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

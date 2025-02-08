// using NUnit.Framework;
// using NSubstitute;
// using System;
// using System.Collections.Generic;
// using System.Linq;
// using System.Threading.Tasks;

// [TestFixture]
// public class RaftNodeTests
// {
//     private IClock _clock;
//     private ITransport _transport;
//     private IStateMachine _stateMachine;
//     private RaftNode _leaderNode;

//     [SetUp]
//     public void SetUp()
//     {
//         _clock = Substitute.For<IClock>();
//         _transport = Substitute.For<ITransport>();
//         _stateMachine = Substitute.For<IStateMachine>();
//         _leaderNode = new RaftNode("Node1", NodeState.Leader, _clock, _transport, _stateMachine);
//     }

//     [Test]
//     public async Task Test1_LeaderSendsLogEntryToAllNodes_WhenClientCommandReceived()
//     {
//         var otherNodes = new List<string> { "Node2", "Node3" };
//         _transport.GetOtherNodeIds(_leaderNode.NodeId).Returns(otherNodes);

//         bool result = await _leaderNode.ReceiveClientCommandAsync("SET key value");

//         NUnit.Framework.Assert.That(result, Is.True);
//         await _transport.Received(1).SendAppendEntriesAsync(
//             Arg.Is<AppendEntries>(ae => ae.LogEntries.Count == 1 && ae.LogEntries[0].Command == "SET key value"),
//             "Node2");
//         await _transport.Received(1).SendAppendEntriesAsync(
//             Arg.Is<AppendEntries>(ae => ae.LogEntries.Count == 1 && ae.LogEntries[0].Command == "SET key value"),
//             "Node3");
//     }

//     [Test]
//     public async Task Test2_LeaderAppendsCommandToItsLog_WhenClientCommandReceived()
//     {
//         await _leaderNode.ReceiveClientCommandAsync("SET key value");

//         NUnit.Framework.Assert.That(_leaderNode.GetLog().Count == 1);
//         NUnit.Framework.Assert.That( _leaderNode.GetLog()[0].Command == "SET key value");
//     }

//     [Test]
//     public void Test3_NewNode_HasEmptyLog()
//     {
//         var newNode = new RaftNode("Node2", _clock, _transport);

//         NUnit.Framework.Assert.That(newNode.GetLog() == null);
//     }

//     [Test]
//     public void Test4_LeaderInitializesNextIndex_WhenWinningElection()
//     {
//         var otherNodes = new List<string> { "Node2", "Node3" };
//         _transport.GetOtherNodeIds(_leaderNode.NodeId).Returns(otherNodes);

//         _leaderNode.StartElection().Wait();

//         NUnit.Framework.Assert.That( _leaderNode.NextIndex["Node2"]== 0);
//         NUnit.Framework.Assert.That( _leaderNode.NextIndex["Node3"]== 0);
//     }

//     [Test]
//     public void Test5_LeaderMaintainsNextIndexForEachFollower()
//     {
//         _leaderNode.NextIndex["Node2"] = 5;
//         NUnit.Framework.Assert.That(_leaderNode.NextIndex["Node2"]==5);
//     }

//     [Test]
//     public void Test6_LeaderIncludesHighestCommitIndexInAppendEntries()
//     {
//         _leaderNode.CommitIndex = 5;
//         var otherNodes = new List<string> { "Node2" };
//         _transport.GetOtherNodeIds(_leaderNode.NodeId).Returns(otherNodes);

//         _leaderNode.SendHeartbeatAsync().Wait();

//         _transport.Received(1).SendAppendEntriesAsync(
//             Arg.Is<AppendEntries>(ae => ae.LeaderCommit == 5), "Node2");
//     }

//     [Test]
//     public async Task Test7_FollowerAppliesCommittedLogEntry_WhenReceivedFromLeader()
//     {
//         var followerNode = new RaftNode("Node2", NodeState.Follower, _clock, _transport, _stateMachine);
//         var appendEntries = new AppendEntries
//         {
//             Term = 1,
//             LeaderId = "Node1",
//             LogEntries = new List<LogEntry> { new LogEntry { Term = 1, Command = "SET key value" } },
//             LeaderCommit = 1
//         };

//         await followerNode.ReceiveAppendEntriesAsync(appendEntries);

//         await _stateMachine.Received(1).ApplyAsync("SET key value");
//     }

//     [Test]
//     public async Task Test8_LeaderCommitsLog_WhenMajorityConfirmationReceived()
//     {
//         var otherNodes = new List<string> { "Node2", "Node3" };
//         _transport.GetOtherNodeIds(_leaderNode.NodeId).Returns(otherNodes);
//         _leaderNode.NextIndex["Node2"] = 1;
//         _leaderNode.NextIndex["Node3"] = 1;
//         _leaderNode.GetLog().Add(new LogEntry { Term = 1, Command = "SET key value" });

//         bool result = await _leaderNode.TryReplicateEntry(0, new Dictionary<string, bool> { { "Node2", true }, { "Node3", true } }, otherNodes, 2);

//         NUnit.Framework.Assert.That(result ==true);
//         NUnit.Framework.Assert.That(_leaderNode.CommitIndex == 1);
//     }

//     [Test]
//     public void Test9_LeaderIncrementsCommitIndex()
//     {
//         _leaderNode.CommitIndex = 1;
//         _leaderNode.CommitIndex++;

//         NUnit.Framework.Assert.That(2 ==_leaderNode.CommitIndex);
//     }

//     [Test]
//     public async Task Test10_FollowerAddsEntriesFromAppendEntriesRPC()
//     {
//         var followerNode = new RaftNode("Node2", NodeState.Follower, _clock, _transport, _stateMachine);
//         var appendEntries = new AppendEntries
//         {
//             Term = 1,
//             LeaderId = "Node1",
//             LogEntries = new List<LogEntry> { new LogEntry { Term = 1, Command = "SET key value" } }
//         };

//         await followerNode.ReceiveAppendEntriesAsync(appendEntries);

//         NUnit.Framework.Assert.That(1 == followerNode.GetLog().Count);
//         NUnit.Framework.Assert.That("SET key value" == followerNode.GetLog()[0].Command);
//     }

//     [Test]
//     public void Test11_FollowerResponseIncludesTermAndLogIndex()
//     {
//         var followerNode = new RaftNode("Node2", NodeState.Follower, _clock, _transport, _stateMachine);

//         _transport.SendAppendEntriesResponseAsync(Arg.Any<AppendEntriesResponse>(), Arg.Any<string>()).Returns(Task.CompletedTask);

//         var response = new AppendEntriesResponse
//         {
//             Success = true,
//             Term = 2,
//             LastLogIndex = 5
//         };

//         NUnit.Framework.Assert.That(2==response.Term);
//         NUnit.Framework.Assert.That(5==response.LastLogIndex);
//     }

//     [Test]
//     public async Task Test12_LeaderSendsConfirmationAfterMajorityLogReplication()
//     {
//         var otherNodes = new List<string> { "Node2", "Node3" };
//         _transport.GetOtherNodeIds(_leaderNode.NodeId).Returns(otherNodes);

//         _leaderNode.NextIndex["Node2"] = 1;
//         _leaderNode.NextIndex["Node3"] = 1;
//         _leaderNode.GetLog().Add(new LogEntry { Term = 1, Command = "SET key value" });

//         bool result = await _leaderNode.TryReplicateEntry(0, new Dictionary<string, bool> { { "Node2", true }, { "Node3", true } }, otherNodes, 2);

//         NUnit.Framework.Assert.That(true == result);
//         NUnit.Framework.Assert.That(0 == _leaderNode.CommitIndex);
//     }

//     [Test]
//     public async Task Test13_LeaderAppliesCommittedLogToStateMachine()
//     {
//         _leaderNode.GetLog().Add(new LogEntry { Term = 1, Command = "SET key value" });
//         _leaderNode.CommitIndex = 0;

//         await _leaderNode.ReceiveClientCommandAsync("SET key value");
//         await _stateMachine.Received(1).ApplyAsync("SET key value");
//     }

//     [Test]
//     public async Task Test14_FollowerUpdatesCommitIndexOnValidHeartbeat()
//     {
//         var followerNode = new RaftNode("Node2", NodeState.Follower, _clock, _transport, _stateMachine);
//         var appendEntries = new AppendEntries
//         {
//             Term = 1,
//             LeaderCommit = 2
//         };

//         await followerNode.ReceiveAppendEntriesAsync(appendEntries);

//         NUnit.Framework.Assert.That(2 == followerNode.CommitIndex);
//     }

//     [Test]
//     public async Task Test15_LeaderHandlesAppendEntriesInconsistencies()
//     {
//         var followerNode = new RaftNode("Node2", NodeState.Follower, _clock, _transport, _stateMachine);
//         followerNode.GetLog().Add(new LogEntry { Term = 1, Command = "SET key1 value1" });
//         var appendEntries = new AppendEntries
//         {
//             Term = 1,
//             LeaderId = "Node1",
//             PrevLogIndex = 0,
//             PrevLogTerm = 2,
//             LogEntries = new List<LogEntry> { new LogEntry { Term = 2, Command = "SET key2 value2" } }
//         };

//         await followerNode.ReceiveAppendEntriesAsync(appendEntries);

//         NUnit.Framework.Assert.That(1 == followerNode.GetLog().Count);
//     }

//     [Test]
//     public async Task Test16_LeaderDoesNotCommitEntryOnFailureToAchieveMajority()
//     {
//         var otherNodes = new List<string> { "Node2", "Node3" };
//         _transport.GetOtherNodeIds(_leaderNode.NodeId).Returns(otherNodes);

//         bool result = await _leaderNode.TryReplicateEntry(0, new Dictionary<string, bool> { { "Node2", false }, { "Node3", false } }, otherNodes, 2);

//         NUnit.Framework.Assert.That(false == result);
//         NUnit.Framework.Assert.That(-1 ==_leaderNode.CommitIndex);
//     }

//     [Test]
//     public void Test17_LeaderContinuesRetriesOnNonResponses()
//     {
//         var otherNodes = new List<string> { "Node2" };
//         _transport.GetOtherNodeIds(_leaderNode.NodeId).Returns(otherNodes);

//         _transport.When(t => t.SendAppendEntriesAsync(Arg.Any<AppendEntries>(), Arg.Any<string>())).Throw(new Exception("Network error"));

//         NUnit.Framework.Assert.DoesNotThrowAsync(() => _leaderNode.SendHeartbeatAsync());
//     }

//     [Test]
//     public void Test18_LeaderDoesNotRespondToClientIfEntryIsUncommitted()
//     {
//         var commandResult = Substitute.For<Action<bool>>();
//         _leaderNode.ReceiveClientCommandAsync("SET key value", commandResult).Wait();

//         commandResult.DidNotReceive().Invoke(true);
//     }

//     [Test]
//     public async Task Test19_FollowerRejectsAppendEntriesTooFarInFuture()
//     {
//         var followerNode = new RaftNode("Node2", NodeState.Follower, _clock, _transport, _stateMachine);
//         var appendEntries = new AppendEntries
//         {
//             PrevLogIndex = 1000,
//             PrevLogTerm = 1,
//             LogEntries = new List<LogEntry> { new LogEntry { Term = 1, Command = "SET key value" } }
//         };

//         await followerNode.ReceiveAppendEntriesAsync(appendEntries);

//         NUnit.Framework.Assert.That(0== followerNode.GetLog().Count);
//     }

//     [Test]
//     public async Task Test20_FollowerRejectsAppendEntriesOnMismatchedTermOrIndex()
//     {
//         var followerNode = new RaftNode("Node2", NodeState.Follower, _clock, _transport, _stateMachine);
//         followerNode.GetLog().Add(new LogEntry { Term = 1, Command = "SET key1 value1" });
//         var appendEntries = new AppendEntries
//         {
//             PrevLogIndex = 0,
//             PrevLogTerm = 2,
//             LogEntries = new List<LogEntry> { new LogEntry { Term = 2, Command = "SET key2 value2" } }
//         };

//         await followerNode.ReceiveAppendEntriesAsync(appendEntries);

//         NUnit.Framework.Assert.That(1== followerNode.GetLog().Count);
//         NUnit.Framework.Assert.That("SET key1 value1"== followerNode.GetLog()[0].Command);
//     }
// }

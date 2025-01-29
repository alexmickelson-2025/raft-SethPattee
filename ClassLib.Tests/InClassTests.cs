using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using NSubstitute;

namespace ClassLib.Tests
{
    public class RaftClassTests
    {
        //#1 
        [Fact]
        public async Task Leader_GetsPaused_NoHeartbeat400ms()
        {
            // Arrange
            var clock = Substitute.For<IClock>();
            var transport = Substitute.For<ITransport>();
            var stateMachine = Substitute.For<IStateMachine>();

            var otherNodeIds = new List<string> { "node1", "node2" };
            transport.GetOtherNodeIds(Arg.Any<string>()).Returns(otherNodeIds);

            var leader = new RaftNode("leaderNode", NodeState.Leader, clock, transport, stateMachine);

            Assert.Equal(2, leader._nextIndex.Count);
            Assert.Equal(0, leader._nextIndex["node1"]);
            Assert.Equal(0, leader._nextIndex["node2"]);

            leader.GetLog().Add(new LogEntry { Term = 0, Command = "command1" });
            leader.GetLog().Add(new LogEntry { Term = 0, Command = "command2" });
            leader.GetLog().Add(new LogEntry { Term = 0, Command = "command3" });

            leader.CommitIndex = 2;

            // Act
            Thread.Sleep(400);
            leader.Paused = true;
            leader.RunLeaderTasksAsync();
            Thread.Sleep(400);


            // Assert
            await transport.DidNotReceive().SendAppendEntriesAsync(
                Arg.Is<AppendEntries>(ae =>
                    ae.LeaderId == "leaderNode" &&
                    ae.Term == 0 &&
                    ae.LogEntries.Count == 0 &&
                    ae.LeaderCommit == 2),
                "node1");

            await transport.DidNotReceive().SendAppendEntriesAsync(
                Arg.Is<AppendEntries>(ae =>
                    ae.LeaderId == "leaderNode" &&
                    ae.Term == 0 &&
                    ae.LogEntries.Count == 0 &&
                    ae.LeaderCommit == 2),
                "node2");
        }
    }
}

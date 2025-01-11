using Xunit;
using NSubstitute;
namespace ClassLib.Tests;
public class SingleCandidateElectionTests
{
    [Fact]
    public void SingleServerBecomesLeaderImmediately()
    {
        // Arrange
        var mockCluster = Substitute.For<ICluster>();
        var server = new Server(mockCluster);

        // Act
        server.Start();

        // Assert
        Assert.True(server.IsLeader, "The server should be the leader.");
        mockCluster.Received(1).SetLeader(server);
    }
}

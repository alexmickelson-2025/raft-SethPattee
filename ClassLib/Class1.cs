namespace ClassLib;

public interface ICluster
{
    void SetLeader(IServer server);
}

public interface IServer
{
    void Start();
    bool IsLeader { get; }
}


public class Server : IServer
{
    private readonly ICluster _cluster;
    public bool IsLeader { get; private set; }

    public Server(ICluster cluster)
    {
        _cluster = cluster;
    }

    public void Start()
    {
        IsLeader = true;
        _cluster.SetLeader(this);
    }
}

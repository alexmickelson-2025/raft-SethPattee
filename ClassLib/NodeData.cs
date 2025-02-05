public class NodeData
{
    public string NodeId { get; set; }
    public NodeState State { get; set; }
    public int Term { get; set; }
    public string? CurrentLeaderId { get; set; }
    public int CommitIndex { get; set; }
    public List<LogEntry> Log { get; set; }
    public bool Paused { get; set; }
}
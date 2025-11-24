namespace GraphData.Core.Models;

public abstract record Edge
{
    public abstract Node Node1 { get; }
    public abstract Node Node2 { get; }
}

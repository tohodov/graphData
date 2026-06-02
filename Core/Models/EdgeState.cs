namespace GraphData.Core.Models;

public abstract class EdgeState
{
    public abstract Node Node1 { get; }
    public abstract Node Node2 { get; }
    public virtual NodeGlobalId? TypeId => null;
}

namespace Oragon.ElasticPool.Core.Tests.TestSupport;

/// <summary>
/// Trivial pooled-object stand-in used across the unit test suite.
/// Each instance carries a sequence id so tests can assert "same instance returned"
/// vs "fresh instance produced".
/// </summary>
public sealed class Resource
{
    private static int _seq;
    public int Id { get; } = Interlocked.Increment(ref _seq);
    public bool BrokenFlag { get; set; }
    public override string ToString() => $"Resource#{Id}";
}

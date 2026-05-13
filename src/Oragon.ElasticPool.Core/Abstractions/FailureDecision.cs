namespace Oragon.ElasticPool.Core.Abstractions;

/// <summary>Action the engine should take based on the failure policy.</summary>
public enum FailureDecision
{
    /// <summary>Discard the item; engine triggers replacement if below MinSize. Default behavior in v1.</summary>
    Discard = 0,
    // Quarantine = 1,  // reserved for v2 (FAIL-V2-01)
}

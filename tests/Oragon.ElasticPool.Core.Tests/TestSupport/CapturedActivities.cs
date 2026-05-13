using System.Diagnostics;

namespace Oragon.ElasticPool.Core.Tests.TestSupport;

/// <summary>
/// Reusable IDisposable helper wrapping <see cref="ActivityListener"/> for span capture in tests.
/// Source: dotnet/runtime test code + Jimmy Bogard "A Lap Around ActivitySource and ActivityListener"
/// (RESEARCH.md §"Pattern 7"). Listener attaches to a single <c>ActivitySource</c> by name and
/// captures every started/stopped Activity for the lifetime of the helper.
/// </summary>
public sealed class CapturedActivities : IDisposable
{
    private readonly ActivityListener _listener;
    private readonly object _lock = new();
    private readonly List<Activity> _started = new();
    private readonly List<Activity> _stopped = new();

    public IReadOnlyList<Activity> Started
    {
        get { lock (_lock) return _started.ToArray(); }
    }

    public IReadOnlyList<Activity> Stopped
    {
        get { lock (_lock) return _stopped.ToArray(); }
    }

    public CapturedActivities(string sourceName)
    {
        _listener = new ActivityListener
        {
            ShouldListenTo = src => src.Name == sourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
            SampleUsingParentId = (ref ActivityCreationOptions<string> _) => ActivitySamplingResult.AllData,
            ActivityStarted = a => { lock (_lock) _started.Add(a); },
            ActivityStopped = a => { lock (_lock) _stopped.Add(a); },
        };
        ActivitySource.AddActivityListener(_listener);
    }

    /// <summary>Returns all stopped activities matching the given operation name.</summary>
    public IReadOnlyList<Activity> ByName(string operationName)
    {
        lock (_lock)
        {
            return _stopped.Where(a => a.OperationName == operationName).ToArray();
        }
    }

    /// <summary>
    /// Returns all stopped activities matching the operation name AND the <c>pool.name</c> tag.
    /// Required for xUnit v3 parallel test runs where a single ActivitySource is shared across
    /// concurrent tests — without filtering, spans from sibling tests bleed into the capture.
    /// </summary>
    public IReadOnlyList<Activity> ByNameAndPool(string operationName, string poolName)
    {
        lock (_lock)
        {
            return _stopped.Where(a =>
                a.OperationName == operationName
                && (string?)a.GetTagItem("pool.name") == poolName).ToArray();
        }
    }

    /// <summary>Returns the single stopped activity matching the operation name (throws if not exactly one).</summary>
    public Activity Single(string operationName) => ByName(operationName).Single();

    public void Dispose() => _listener.Dispose();
}

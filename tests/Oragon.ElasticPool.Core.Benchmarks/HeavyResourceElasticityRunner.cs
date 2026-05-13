using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.ObjectPool;
using Oragon.ElasticPool.Core.Abstractions;
using Oragon.ElasticPool.Core.Builder;

namespace Oragon.ElasticPool.Core.Benchmarks;

internal static class HeavyResourceElasticityRunner
{
    private const int DefaultResourceMegabytes = 10;
    private const int DefaultCreationDelayMilliseconds = 50;
    private const int DefaultWorkMilliseconds = 15;
    private const int DefaultMaxResources = 256;

    private static readonly int[] DemandCurve = [1, 10, 100, 1_000, 10_000, 20_000, 30_000, 20_000, 10_000, 1_000, 100, 10, 1];

    public static async Task RunAsync(string[] args)
    {
        if (args.Any(static arg => string.Equals(arg, "--help", StringComparison.OrdinalIgnoreCase)
                                   || string.Equals(arg, "-h", StringComparison.OrdinalIgnoreCase)))
        {
            Console.WriteLine("Usage: dotnet run --project tests/Oragon.ElasticPool.Core.Benchmarks -c Release -f net10.0 -- elasticity [--profile smoke|readme|real]");
            Console.WriteLine("Profiles: smoke validates the runner quickly; readme is the default report profile; real runs longer phases and cooldown.");
            return;
        }

        var profile = ElasticityProfile.FromArgs(args);
        var reportDirectory = Path.Combine(Environment.CurrentDirectory, "BenchmarkReports");
        Directory.CreateDirectory(reportDirectory);

        Console.WriteLine("Heavy resource elasticity benchmark");
        Console.WriteLine(
            string.Create(CultureInfo.InvariantCulture,
                $"Profile={profile.Name}; Resource={profile.ResourceMegabytes} MB; CreateDelay={DefaultCreationDelayMilliseconds} ms; Work={DefaultWorkMilliseconds} ms; MaxInFlight={profile.MaxInFlight}"));
        Console.WriteLine("Strategies: NoPool, ElasticPool, Microsoft.Extensions.ObjectPool");
        Console.WriteLine();

        var allReports = new List<StrategyReport>();
        foreach (var strategyFactory in CreateStrategyFactories(profile))
        {
            ForceFullCollection();
            var strategy = strategyFactory();
            try
            {
                await strategy.WarmAsync(CancellationToken.None).ConfigureAwait(false);
                var report = await RunStrategyAsync(strategy, profile).ConfigureAwait(false);
                allReports.Add(report);
                WriteConsoleSummary(report);
            }
            finally
            {
                await strategy.DisposeAsync().ConfigureAwait(false);
                ForceFullCollection();
            }
        }

        var csvPath = Path.Combine(reportDirectory, "heavy-resource-elasticity.csv");
        var markdownPath = Path.Combine(reportDirectory, "heavy-resource-elasticity.md");
        await File.WriteAllTextAsync(csvPath, RenderCsv(allReports, profile), Encoding.UTF8).ConfigureAwait(false);
        await File.WriteAllTextAsync(markdownPath, RenderMarkdown(allReports, profile), Encoding.UTF8).ConfigureAwait(false);

        Console.WriteLine();
        Console.WriteLine($"Wrote {csvPath}");
        Console.WriteLine($"Wrote {markdownPath}");
    }

    private static IEnumerable<Func<IResourceStrategy>> CreateStrategyFactories(ElasticityProfile profile)
    {
        yield return () => new NoPoolStrategy(profile.ResourceMegabytes);
        yield return () => new ElasticPoolStrategy(profile.ResourceMegabytes);
        yield return () => new MicrosoftObjectPoolStrategy(profile.ResourceMegabytes);
    }

    private static async Task<StrategyReport> RunStrategyAsync(IResourceStrategy strategy, ElasticityProfile profile)
    {
        Console.WriteLine($"Running {strategy.Name}...");

        var phaseReports = new List<PhaseReport>();
        for (var phaseIndex = 0; phaseIndex < profile.Rates.Length; phaseIndex++)
        {
            var requestedRate = profile.Rates[phaseIndex];
            var phase = await RunPhaseAsync(strategy, profile, phaseIndex, requestedRate).ConfigureAwait(false);
            phaseReports.Add(phase);
            Console.WriteLine(
                string.Create(CultureInfo.InvariantCulture,
                    $"  {requestedRate,5:N0} req/s -> achieved {phase.AchievedRequestsPerSecond,8:N0} req/s; p95 {phase.P95Milliseconds,7:N2} ms; live {phase.LiveResources,4}; retained {phase.LogicalRetainedMegabytes,5:N0} MB; skipped {phase.Skipped:N0}"));
        }

        if (profile.Cooldown > TimeSpan.Zero)
        {
            await Task.Delay(profile.Cooldown).ConfigureAwait(false);
        }

        return new StrategyReport(strategy.Name, phaseReports, strategy.Tracker.Snapshot(), CaptureProcessSnapshot(), strategy.Snapshot());
    }

    private static async Task<PhaseReport> RunPhaseAsync(IResourceStrategy strategy, ElasticityProfile profile, int phaseIndex, int requestedRate)
    {
        var phaseStats = new PhaseStats();
        var beforeResources = strategy.Tracker.Snapshot();
        var sampler = new PhaseSampler(profile.ResourceMegabytes, Stopwatch.GetTimestamp(), strategy.Snapshot());
        var limiter = new SemaphoreSlim(profile.MaxInFlight, profile.MaxInFlight);
        var tasks = new ConcurrentBag<Task>();
        var startedAt = Stopwatch.GetTimestamp();
        var attemptedArrivals = 0L;

        while (Stopwatch.GetElapsedTime(startedAt) < profile.PhaseDuration)
        {
            var elapsed = Stopwatch.GetElapsedTime(startedAt);
            var targetArrivals = (long)Math.Floor(requestedRate * Math.Min(elapsed.TotalSeconds, profile.PhaseDuration.TotalSeconds));
            LaunchArrivals(targetArrivals - attemptedArrivals);
            attemptedArrivals = targetArrivals;
            sampler.Sample(Stopwatch.GetTimestamp(), strategy.Snapshot());

            await Task.Delay(1).ConfigureAwait(false);
        }

        var finalTargetArrivals = (long)Math.Round(requestedRate * profile.PhaseDuration.TotalSeconds);
        LaunchArrivals(finalTargetArrivals - attemptedArrivals);
        sampler.Sample(Stopwatch.GetTimestamp(), strategy.Snapshot());

        void LaunchArrivals(long toStart)
        {
            for (var i = 0L; i < toStart; i++)
            {
                if (!limiter.Wait(0))
                {
                    phaseStats.MarkSkipped();
                    continue;
                }

                phaseStats.MarkStarted();
                tasks.Add(Task.Run(async () =>
                {
                    var requestStart = Stopwatch.GetTimestamp();
                    try
                    {
                        await using var lease = await strategy.AcquireAsync(CancellationToken.None).ConfigureAwait(false);
                        await Task.Delay(DefaultWorkMilliseconds).ConfigureAwait(false);
                        var elapsed = Stopwatch.GetElapsedTime(requestStart);
                        phaseStats.MarkCompleted(elapsed);
                    }
                    catch
                    {
                        phaseStats.MarkError();
                    }
                    finally
                    {
                        limiter.Release();
                    }
                }));
            }
        }

        await Task.WhenAll(tasks).ConfigureAwait(false);
        var process = CaptureProcessSnapshot();
        var resources = strategy.Tracker.Snapshot();
        var pool = strategy.Snapshot();
        sampler.Sample(Stopwatch.GetTimestamp(), pool);
        var latencies = phaseStats.GetLatencies();

        return new PhaseReport(
            phaseIndex,
            requestedRate,
            profile.ResourceMegabytes,
            profile.PhaseDuration,
            phaseStats.Started,
            phaseStats.Completed,
            phaseStats.Errors,
            phaseStats.Skipped,
            Percentile(latencies, 0.50),
            Percentile(latencies, 0.95),
            Percentile(latencies, 0.99),
            resources.Created,
            resources.Disposed,
            resources.Created - beforeResources.Created,
            resources.Disposed - beforeResources.Disposed,
            resources.Live,
            resources.PeakLive,
            process.ManagedBytes,
            process.WorkingSetBytes,
            GC.CollectionCount(0),
            GC.CollectionCount(1),
            GC.CollectionCount(2),
            pool.Total,
            pool.Available,
            pool.InUse,
            pool.Waiting,
            sampler.PeakPoolTotal,
            sampler.PeakPoolInUse,
            sampler.PeakPoolWaiting,
            sampler.RetainedMegabyteSeconds,
            sampler.SampledSeconds);
    }

    private static ProcessSnapshot CaptureProcessSnapshot()
    {
        using var current = Process.GetCurrentProcess();
        return new ProcessSnapshot(GC.GetTotalMemory(false), current.WorkingSet64);
    }

    private static double Percentile(IReadOnlyList<double> sortedMilliseconds, double percentile)
    {
        if (sortedMilliseconds.Count == 0)
            return 0;

        var index = (int)Math.Ceiling(percentile * sortedMilliseconds.Count) - 1;
        index = Math.Clamp(index, 0, sortedMilliseconds.Count - 1);
        return sortedMilliseconds[index];
    }

    private static void ForceFullCollection()
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
    }

    private static void WriteConsoleSummary(StrategyReport report)
    {
        var last = report.Phases[^1];
        var finalLogicalRetainedMegabytes = report.Resources.Live * last.ResourceMegabytes;
        var finalLogicalDisposedMegabytes = report.Resources.Disposed * last.ResourceMegabytes;
        Console.WriteLine(
            string.Create(CultureInfo.InvariantCulture,
                $"Finished {report.Strategy}: created={report.Resources.Created:N0}, disposed={report.Resources.Disposed:N0}, live={report.Resources.Live:N0}, retained={finalLogicalRetainedMegabytes:N0} MB logical, discarded={finalLogicalDisposedMegabytes:N0} MB logical, finalPoolTotal={report.Pool.Total}"));
        Console.WriteLine(
            string.Create(CultureInfo.InvariantCulture,
                $"  Final phase achieved={last.AchievedRequestsPerSecond:N0} req/s, p95={last.P95Milliseconds:N2} ms; managed={ToMegabytes(report.Process.ManagedBytes):N1} MB; workingSet={ToMegabytes(report.Process.WorkingSetBytes):N1} MB"));
        Console.WriteLine();
    }

    private static string RenderCsv(IReadOnlyList<StrategyReport> reports, ElasticityProfile profile)
    {
        var sb = new StringBuilder();
        var adaptiveByPhase = GetAdaptivePhasesByIndex(reports);
        sb.AppendLine("strategy,phase_index,requested_rps,achieved_rps,started,completed,errors,skipped,p50_ms,p95_ms,p99_ms,created,disposed,created_during_phase,disposed_during_phase,live,peak_live,logical_retained_mb,logical_peak_mb,logical_disposed_mb,pool_retained_mb,retained_mb_seconds,sampled_seconds,avg_pool_retained_mb,peak_pool_total,peak_pool_in_use,peak_pool_waiting,adaptive_pool_retained_mb,objectpool_vs_adaptive_retained_factor,objectpool_excess_retained_mb,managed_mb,working_set_mb,gen0,gen1,gen2,pool_total,pool_available,pool_in_use,pool_waiting");
        foreach (var report in reports)
        {
            foreach (var phase in report.Phases)
            {
                var adaptiveRetained = adaptiveByPhase.TryGetValue(phase.PhaseIndex, out var adaptivePhase)
                    ? adaptivePhase.PoolRetainedMegabytes
                    : 0;
                var isObjectPool = IsMicrosoftObjectPool(report.Strategy);
                var retainedFactor = isObjectPool && adaptiveRetained > 0
                    ? phase.PoolRetainedMegabytes / adaptiveRetained
                    : (double?)null;
                var excessRetained = isObjectPool
                    ? phase.PoolRetainedMegabytes - adaptiveRetained
                    : (double?)null;

                sb.Append(Csv(report.Strategy)).Append(',')
                    .Append(phase.PhaseIndex).Append(',')
                    .Append(phase.RequestedRequestsPerSecond).Append(',')
                    .Append(Invariant(phase.AchievedRequestsPerSecond)).Append(',')
                    .Append(phase.Started).Append(',')
                    .Append(phase.Completed).Append(',')
                    .Append(phase.Errors).Append(',')
                    .Append(phase.Skipped).Append(',')
                    .Append(Invariant(phase.P50Milliseconds)).Append(',')
                    .Append(Invariant(phase.P95Milliseconds)).Append(',')
                    .Append(Invariant(phase.P99Milliseconds)).Append(',')
                    .Append(phase.CreatedResources).Append(',')
                    .Append(phase.DisposedResources).Append(',')
                    .Append(phase.CreatedDuringPhase).Append(',')
                    .Append(phase.DisposedDuringPhase).Append(',')
                    .Append(phase.LiveResources).Append(',')
                    .Append(phase.PeakLiveResources).Append(',')
                    .Append(Invariant(phase.LogicalRetainedMegabytes)).Append(',')
                    .Append(Invariant(phase.LogicalPeakMegabytes)).Append(',')
                    .Append(Invariant(phase.LogicalDisposedMegabytes)).Append(',')
                    .Append(Invariant(phase.PoolRetainedMegabytes)).Append(',')
                    .Append(Invariant(phase.RetainedMegabyteSeconds)).Append(',')
                    .Append(Invariant(phase.SampledSeconds)).Append(',')
                    .Append(Invariant(phase.AveragePoolRetainedMegabytes)).Append(',')
                    .Append(phase.PeakPoolTotal).Append(',')
                    .Append(phase.PeakPoolInUse).Append(',')
                    .Append(phase.PeakPoolWaiting).Append(',')
                    .Append(Invariant(adaptiveRetained)).Append(',')
                    .Append(retainedFactor.HasValue ? Invariant(retainedFactor.Value) : string.Empty).Append(',')
                    .Append(excessRetained.HasValue ? Invariant(excessRetained.Value) : string.Empty).Append(',')
                    .Append(Invariant(ToMegabytes(phase.ManagedBytes))).Append(',')
                    .Append(Invariant(ToMegabytes(phase.WorkingSetBytes))).Append(',')
                    .Append(phase.Gen0Collections).Append(',')
                    .Append(phase.Gen1Collections).Append(',')
                    .Append(phase.Gen2Collections).Append(',')
                    .Append(phase.PoolTotal).Append(',')
                    .Append(phase.PoolAvailable).Append(',')
                    .Append(phase.PoolInUse).Append(',')
                    .Append(phase.PoolWaiting)
                    .AppendLine();
            }
        }

        return sb.ToString();
    }

    private static string RenderMarkdown(IReadOnlyList<StrategyReport> reports, ElasticityProfile profile)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# Heavy Resource Elasticity Benchmark");
        sb.AppendLine();
        sb.AppendLine(CultureInfo.InvariantCulture,
            $"- Profile: `{profile.Name}`");
        sb.AppendLine(CultureInfo.InvariantCulture,
            $"- Resource cost: `{profile.ResourceMegabytes} MB` per instance, `{DefaultCreationDelayMilliseconds} ms` creation delay, `{DefaultWorkMilliseconds} ms` request hold time");
        sb.AppendLine(CultureInfo.InvariantCulture,
            $"- Max in-flight requests: `{profile.MaxInFlight}`");
        sb.AppendLine();
        sb.AppendLine("Primary metric: `logical retained MB = live instances x resource MB`. Managed heap and working set are process-level hints and may lag behind object disposal.");
        sb.AppendLine();
        sb.AppendLine("| Strategy | Phase | Requested req/s | Achieved req/s | p95 ms | Created in phase | Disposed in phase | Live | Pool retained MB | Avg retained MB | MB*s retained | Peak pool total | Skipped |");
        sb.AppendLine("|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|");
        foreach (var report in reports)
        {
            foreach (var phase in report.Phases)
            {
                sb.Append("| ")
                    .Append(report.Strategy).Append(" | ")
                    .Append(phase.PhaseIndex).Append(" | ")
                    .Append(phase.RequestedRequestsPerSecond.ToString("N0", CultureInfo.InvariantCulture)).Append(" | ")
                    .Append(phase.AchievedRequestsPerSecond.ToString("N0", CultureInfo.InvariantCulture)).Append(" | ")
                    .Append(phase.P95Milliseconds.ToString("N2", CultureInfo.InvariantCulture)).Append(" | ")
                    .Append(phase.CreatedDuringPhase.ToString("N0", CultureInfo.InvariantCulture)).Append(" | ")
                    .Append(phase.DisposedDuringPhase.ToString("N0", CultureInfo.InvariantCulture)).Append(" | ")
                    .Append(phase.LiveResources.ToString("N0", CultureInfo.InvariantCulture)).Append(" | ")
                    .Append(phase.PoolRetainedMegabytes.ToString("N0", CultureInfo.InvariantCulture)).Append(" | ")
                    .Append(phase.AveragePoolRetainedMegabytes.ToString("N0", CultureInfo.InvariantCulture)).Append(" | ")
                    .Append(phase.RetainedMegabyteSeconds.ToString("N0", CultureInfo.InvariantCulture)).Append(" | ")
                    .Append(phase.PeakPoolTotal.ToString("N0", CultureInfo.InvariantCulture)).Append(" | ")
                    .Append(phase.Skipped.ToString("N0", CultureInfo.InvariantCulture)).AppendLine(" |");
            }
        }

        AppendMeasuredElasticitySummary(sb, reports);
        AppendAdaptiveVsObjectPoolSummary(sb, reports);

        sb.AppendLine();
        sb.AppendLine("## Post-cooldown summary");
        sb.AppendLine();
        sb.AppendLine("| Strategy | Created | Disposed | Live | Logical retained MB | Logical disposed MB | Final managed MB | Final working set MB | Final pool total |");
        sb.AppendLine("|---|---:|---:|---:|---:|---:|---:|---:|---:|");
        foreach (var report in reports)
        {
            var finalLogicalRetainedMegabytes = report.Resources.Live * profile.ResourceMegabytes;
            var finalLogicalDisposedMegabytes = report.Resources.Disposed * profile.ResourceMegabytes;
            sb.Append("| ")
                .Append(report.Strategy).Append(" | ")
                .Append(report.Resources.Created.ToString("N0", CultureInfo.InvariantCulture)).Append(" | ")
                .Append(report.Resources.Disposed.ToString("N0", CultureInfo.InvariantCulture)).Append(" | ")
                .Append(report.Resources.Live.ToString("N0", CultureInfo.InvariantCulture)).Append(" | ")
                .Append(finalLogicalRetainedMegabytes.ToString("N0", CultureInfo.InvariantCulture)).Append(" | ")
                .Append(finalLogicalDisposedMegabytes.ToString("N0", CultureInfo.InvariantCulture)).Append(" | ")
                .Append(ToMegabytes(report.Process.ManagedBytes).ToString("N1", CultureInfo.InvariantCulture)).Append(" | ")
                .Append(ToMegabytes(report.Process.WorkingSetBytes).ToString("N1", CultureInfo.InvariantCulture)).Append(" | ")
                .Append(report.Pool.Total.ToString("N0", CultureInfo.InvariantCulture)).AppendLine(" |");
        }

        return sb.ToString();
    }

    private static string Csv(string value) => "\"" + value.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";
    private static string Invariant(double value) => value.ToString("G17", CultureInfo.InvariantCulture);
    private static double ToMegabytes(long bytes) => bytes / 1024d / 1024d;

    private static Dictionary<int, PhaseReport> GetAdaptivePhasesByIndex(IEnumerable<StrategyReport> reports) =>
        reports
            .FirstOrDefault(static report => string.Equals(report.Strategy, "ElasticPool", StringComparison.Ordinal))
            ?.Phases.ToDictionary(static phase => phase.PhaseIndex)
        ?? [];

    private static bool IsMicrosoftObjectPool(string strategy) =>
        string.Equals(strategy, "Microsoft.Extensions.ObjectPool", StringComparison.Ordinal);

    private static void AppendMeasuredElasticitySummary(StringBuilder sb, IReadOnlyList<StrategyReport> reports)
    {
        sb.AppendLine();
        sb.AppendLine("## Measured elasticity");
        sb.AppendLine();
        sb.AppendLine("| Strategy | Created during run | Disposed during run | Peak pool total | Retained MB*s | Average retained MB | Final retained MB |");
        sb.AppendLine("|---|---:|---:|---:|---:|---:|---:|");

        foreach (var report in reports)
        {
            var retainedMegabyteSeconds = report.Phases.Sum(static phase => phase.RetainedMegabyteSeconds);
            var elapsedSeconds = report.Phases.Sum(static phase => phase.SampledSeconds);
            var averageRetainedMegabytes = elapsedSeconds > 0
                ? retainedMegabyteSeconds / elapsedSeconds
                : 0;
            var peakPoolTotal = report.Phases.Count > 0
                ? report.Phases.Max(static phase => phase.PeakPoolTotal)
                : 0;
            var finalRetainedMegabytes = report.Resources.Live * report.Phases[^1].ResourceMegabytes;

            sb.Append("| ")
                .Append(report.Strategy).Append(" | ")
                .Append(report.Resources.Created.ToString("N0", CultureInfo.InvariantCulture)).Append(" | ")
                .Append(report.Resources.Disposed.ToString("N0", CultureInfo.InvariantCulture)).Append(" | ")
                .Append(peakPoolTotal.ToString("N0", CultureInfo.InvariantCulture)).Append(" | ")
                .Append(retainedMegabyteSeconds.ToString("N0", CultureInfo.InvariantCulture)).Append(" | ")
                .Append(averageRetainedMegabytes.ToString("N0", CultureInfo.InvariantCulture)).Append(" | ")
                .Append(finalRetainedMegabytes.ToString("N0", CultureInfo.InvariantCulture)).AppendLine(" |");
        }
    }

    private static void AppendAdaptiveVsObjectPoolSummary(StringBuilder sb, IReadOnlyList<StrategyReport> reports)
    {
        var adaptive = reports.FirstOrDefault(static report => string.Equals(report.Strategy, "ElasticPool", StringComparison.Ordinal));
        var objectPool = reports.FirstOrDefault(static report => IsMicrosoftObjectPool(report.Strategy));
        if (adaptive is null || objectPool is null || adaptive.Phases.Count == 0 || objectPool.Phases.Count == 0)
            return;

        sb.AppendLine();
        sb.AppendLine("## ElasticPool vs ObjectPool logical retention");
        sb.AppendLine();
        sb.AppendLine("| Phase | Requested req/s | ElasticPool retained MB | ObjectPool retained MB | ObjectPool excess MB | Retention factor |");
        sb.AppendLine("|---:|---:|---:|---:|---:|---:|");

        for (var i = 0; i < Math.Min(adaptive.Phases.Count, objectPool.Phases.Count); i++)
        {
            var adaptivePhase = adaptive.Phases[i];
            var objectPoolPhase = objectPool.Phases[i];
            var factor = adaptivePhase.PoolRetainedMegabytes > 0
                ? objectPoolPhase.PoolRetainedMegabytes / adaptivePhase.PoolRetainedMegabytes
                : 0;
            var excess = objectPoolPhase.PoolRetainedMegabytes - adaptivePhase.PoolRetainedMegabytes;

            sb.Append("| ")
                .Append(adaptivePhase.PhaseIndex).Append(" | ")
                .Append(adaptivePhase.RequestedRequestsPerSecond.ToString("N0", CultureInfo.InvariantCulture)).Append(" | ")
                .Append(adaptivePhase.PoolRetainedMegabytes.ToString("N0", CultureInfo.InvariantCulture)).Append(" | ")
                .Append(objectPoolPhase.PoolRetainedMegabytes.ToString("N0", CultureInfo.InvariantCulture)).Append(" | ")
                .Append(excess.ToString("N0", CultureInfo.InvariantCulture)).Append(" | ")
                .Append(factor.ToString("N1", CultureInfo.InvariantCulture)).Append("x |")
                .AppendLine();
        }
    }

    private sealed record ElasticityProfile(
        string Name,
        int[] Rates,
        TimeSpan PhaseDuration,
        TimeSpan Cooldown,
        int ResourceMegabytes,
        int MaxInFlight)
    {
        public static ElasticityProfile FromArgs(string[] args)
        {
            var name = "readme";
            for (var i = 0; i < args.Length; i++)
            {
                if (string.Equals(args[i], "--profile", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
                {
                    name = args[i + 1];
                    i++;
                }
            }

            return name.ToLowerInvariant() switch
            {
                "smoke" => new ElasticityProfile("smoke", [1, 10, 100], TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1), 1, 16),
                "real" => new ElasticityProfile("real", DemandCurve, TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(15), DefaultResourceMegabytes, DefaultMaxResources),
                "readme" => new ElasticityProfile("readme", DemandCurve, TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(10), DefaultResourceMegabytes, DefaultMaxResources),
                _ => throw new ArgumentException($"Unknown elasticity benchmark profile '{name}'. Use smoke, readme, or real.")
            };
        }
    }

    private interface IResourceStrategy : IAsyncDisposable
    {
        string Name { get; }
        HeavyResourceTracker Tracker { get; }
        ValueTask WarmAsync(CancellationToken cancellationToken);
        ValueTask<ResourceLease> AcquireAsync(CancellationToken cancellationToken);
        PoolSnapshot Snapshot();
    }

    private sealed class NoPoolStrategy(int resourceMegabytes) : IResourceStrategy
    {
        public string Name => "NoPool";
        public HeavyResourceTracker Tracker { get; } = new();

        public ValueTask WarmAsync(CancellationToken cancellationToken) => ValueTask.CompletedTask;

        public async ValueTask<ResourceLease> AcquireAsync(CancellationToken cancellationToken)
        {
            var resource = await HeavyResource.CreateAsync(resourceMegabytes, Tracker, cancellationToken).ConfigureAwait(false);
            return new ResourceLease(() =>
            {
                resource.Dispose();
                return ValueTask.CompletedTask;
            });
        }

        public PoolSnapshot Snapshot() => PoolSnapshot.Empty;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class ElasticPoolStrategy : IResourceStrategy
    {
        private readonly IElasticPool<HeavyResource> _pool;
        public string Name => "ElasticPool";
        public HeavyResourceTracker Tracker { get; } = new();

        public ElasticPoolStrategy(int resourceMegabytes)
        {
            _pool = ElasticObjectPoolFactory.Build<HeavyResource>(new ServiceCollection().BuildServiceProvider())
                .Factory(async (_, ct) => await HeavyResource.CreateAsync(resourceMegabytes, Tracker, ct).ConfigureAwait(false))
                .Release((resource, _) => resource.Dispose())
                .WithBounds(minSize: 0, maxSize: DefaultMaxResources, initialSize: 0)
                .IdleTimeout(TimeSpan.FromSeconds(3))
                .SweepInterval(TimeSpan.FromSeconds(1))
                .ShrinkOnUtilizationPercent(0.60)
                .ShrinkTargetUtilizationPercent(0.75)
                .ShrinkBatchSize(32)
                .ShrinkCooldownWindows(1)
                .Build();
        }

        public async ValueTask WarmAsync(CancellationToken cancellationToken)
        {
            await _pool.ReadyAsync().ConfigureAwait(false);
        }

        public async ValueTask<ResourceLease> AcquireAsync(CancellationToken cancellationToken)
        {
            var lease = await _pool.AcquireAsync(cancellationToken).ConfigureAwait(false);
            return new ResourceLease(() => lease.DisposeAsync());
        }

        public PoolSnapshot Snapshot() => new(_pool.Total, _pool.Available, _pool.InUse, _pool.Waiting);
        public async ValueTask DisposeAsync() => await _pool.DisposeAsync().ConfigureAwait(false);
    }

    private sealed class MicrosoftObjectPoolStrategy : IResourceStrategy
    {
        private readonly TrackingHeavyResourcePolicy _policy;
        private readonly ObjectPool<HeavyResource> _pool;
        public string Name => "Microsoft.Extensions.ObjectPool";
        public HeavyResourceTracker Tracker { get; } = new();

        public MicrosoftObjectPoolStrategy(int resourceMegabytes)
        {
            _policy = new TrackingHeavyResourcePolicy(resourceMegabytes, Tracker);
            var provider = new DefaultObjectPoolProvider { MaximumRetained = DefaultMaxResources };
            _pool = provider.Create(_policy);
        }

        public ValueTask WarmAsync(CancellationToken cancellationToken) => ValueTask.CompletedTask;

        public ValueTask<ResourceLease> AcquireAsync(CancellationToken cancellationToken)
        {
            var resource = _pool.Get();
            return new ValueTask<ResourceLease>(new ResourceLease(() =>
            {
                _pool.Return(resource);
                return ValueTask.CompletedTask;
            }));
        }

        public PoolSnapshot Snapshot()
        {
            var stats = Tracker.Snapshot();
            return new PoolSnapshot(checked((int)stats.Live), 0, 0, 0);
        }

        public ValueTask DisposeAsync()
        {
            _policy.DisposeCreatedResources();
            return ValueTask.CompletedTask;
        }
    }

    private sealed class TrackingHeavyResourcePolicy(int resourceMegabytes, HeavyResourceTracker tracker) : PooledObjectPolicy<HeavyResource>
    {
        private readonly ConcurrentBag<HeavyResource> _created = [];

        public override HeavyResource Create()
        {
            Thread.Sleep(DefaultCreationDelayMilliseconds);
            var resource = new HeavyResource(resourceMegabytes, tracker);
            _created.Add(resource);
            return resource;
        }

        public override bool Return(HeavyResource obj) => true;

        public void DisposeCreatedResources()
        {
            foreach (var resource in _created)
            {
                resource.Dispose();
            }
        }
    }

    private sealed class HeavyResource : IDisposable
    {
        private readonly HeavyResourceTracker _tracker;
        private readonly byte[] _payload;
        private int _disposed;

        public HeavyResource(int megabytes, HeavyResourceTracker tracker)
        {
            _tracker = tracker;
            _payload = new byte[megabytes * 1024 * 1024];
            for (var i = 0; i < _payload.Length; i += 4096)
            {
                _payload[i] = 1;
            }
            _payload[^1] = 1;
            _tracker.MarkCreated();
        }

        public static async ValueTask<HeavyResource> CreateAsync(int megabytes, HeavyResourceTracker tracker, CancellationToken cancellationToken)
        {
            await Task.Delay(DefaultCreationDelayMilliseconds, cancellationToken).ConfigureAwait(false);
            return new HeavyResource(megabytes, tracker);
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
                return;

            _tracker.MarkDisposed();
        }
    }

    private sealed class HeavyResourceTracker
    {
        private long _created;
        private long _disposed;
        private long _live;
        private long _peakLive;

        public void MarkCreated()
        {
            Interlocked.Increment(ref _created);
            var live = Interlocked.Increment(ref _live);
            while (true)
            {
                var currentPeak = Volatile.Read(ref _peakLive);
                if (live <= currentPeak)
                    return;
                if (Interlocked.CompareExchange(ref _peakLive, live, currentPeak) == currentPeak)
                    return;
            }
        }

        public void MarkDisposed()
        {
            Interlocked.Increment(ref _disposed);
            Interlocked.Decrement(ref _live);
        }

        public ResourceSnapshot Snapshot() =>
            new(Volatile.Read(ref _created), Volatile.Read(ref _disposed), Volatile.Read(ref _live), Volatile.Read(ref _peakLive));
    }

    private sealed class ResourceLease : IAsyncDisposable
    {
        private readonly Func<ValueTask> _dispose;
        private int _disposed;

        public ResourceLease(Func<ValueTask> dispose)
        {
            _dispose = dispose;
        }

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
                return;

            await _dispose().ConfigureAwait(false);
        }
    }

    private sealed class PhaseStats
    {
        private readonly List<double> _latencies = [];
        private readonly object _latencyGate = new();
        private long _started;
        private long _completed;
        private long _errors;
        private long _skipped;

        public long Started => Volatile.Read(ref _started);
        public long Completed => Volatile.Read(ref _completed);
        public long Errors => Volatile.Read(ref _errors);
        public long Skipped => Volatile.Read(ref _skipped);

        public void MarkStarted() => Interlocked.Increment(ref _started);
        public void MarkSkipped() => Interlocked.Increment(ref _skipped);
        public void MarkError() => Interlocked.Increment(ref _errors);

        public void MarkCompleted(TimeSpan elapsed)
        {
            Interlocked.Increment(ref _completed);
            lock (_latencyGate)
            {
                _latencies.Add(elapsed.TotalMilliseconds);
            }
        }

        public double[] GetLatencies()
        {
            lock (_latencyGate)
            {
                var values = _latencies.ToArray();
                Array.Sort(values);
                return values;
            }
        }
    }

    private sealed class PhaseSampler
    {
        private readonly int _resourceMegabytes;
        private long _lastTimestamp;
        private PoolSnapshot _lastSnapshot;

        public PhaseSampler(int resourceMegabytes, long timestamp, PoolSnapshot snapshot)
        {
            _resourceMegabytes = resourceMegabytes;
            _lastTimestamp = timestamp;
            _lastSnapshot = snapshot;
            PeakPoolTotal = snapshot.Total;
            PeakPoolInUse = snapshot.InUse;
            PeakPoolWaiting = snapshot.Waiting;
        }

        public int PeakPoolTotal { get; private set; }
        public int PeakPoolInUse { get; private set; }
        public int PeakPoolWaiting { get; private set; }
        public double RetainedMegabyteSeconds { get; private set; }
        public double SampledSeconds { get; private set; }

        public void Sample(long timestamp, PoolSnapshot snapshot)
        {
            var elapsed = Stopwatch.GetElapsedTime(_lastTimestamp, timestamp);
            SampledSeconds += elapsed.TotalSeconds;
            RetainedMegabyteSeconds += _lastSnapshot.Total * _resourceMegabytes * elapsed.TotalSeconds;
            PeakPoolTotal = Math.Max(PeakPoolTotal, snapshot.Total);
            PeakPoolInUse = Math.Max(PeakPoolInUse, snapshot.InUse);
            PeakPoolWaiting = Math.Max(PeakPoolWaiting, snapshot.Waiting);
            _lastTimestamp = timestamp;
            _lastSnapshot = snapshot;
        }
    }

    private readonly record struct ResourceSnapshot(long Created, long Disposed, long Live, long PeakLive);
    private readonly record struct ProcessSnapshot(long ManagedBytes, long WorkingSetBytes);
    private readonly record struct PoolSnapshot(int Total, int Available, int InUse, int Waiting)
    {
        public static PoolSnapshot Empty { get; } = new(0, 0, 0, 0);
    }

    private sealed record StrategyReport(
        string Strategy,
        IReadOnlyList<PhaseReport> Phases,
        ResourceSnapshot Resources,
        ProcessSnapshot Process,
        PoolSnapshot Pool);

    private sealed record PhaseReport(
        int PhaseIndex,
        int RequestedRequestsPerSecond,
        int ResourceMegabytes,
        TimeSpan Elapsed,
        long Started,
        long Completed,
        long Errors,
        long Skipped,
        double P50Milliseconds,
        double P95Milliseconds,
        double P99Milliseconds,
        long CreatedResources,
        long DisposedResources,
        long CreatedDuringPhase,
        long DisposedDuringPhase,
        long LiveResources,
        long PeakLiveResources,
        long ManagedBytes,
        long WorkingSetBytes,
        int Gen0Collections,
        int Gen1Collections,
        int Gen2Collections,
        int PoolTotal,
        int PoolAvailable,
        int PoolInUse,
        int PoolWaiting,
        int PeakPoolTotal,
        int PeakPoolInUse,
        int PeakPoolWaiting,
        double RetainedMegabyteSeconds,
        double SampledSeconds)
    {
        public double AchievedRequestsPerSecond => Completed / Math.Max(Elapsed.TotalSeconds, 0.001);
        public double LogicalRetainedMegabytes => LiveResources * ResourceMegabytes;
        public double LogicalPeakMegabytes => PeakLiveResources * ResourceMegabytes;
        public double LogicalDisposedMegabytes => DisposedResources * ResourceMegabytes;
        public double PoolRetainedMegabytes => PoolTotal * ResourceMegabytes;
        public double AveragePoolRetainedMegabytes => RetainedMegabyteSeconds / Math.Max(SampledSeconds, 0.001);
    }
}

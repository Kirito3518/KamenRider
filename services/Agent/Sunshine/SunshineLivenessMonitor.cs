using KamenRaider.Agent.Configuration;
using Microsoft.Extensions.Options;

namespace KamenRaider.Agent.Sunshine;

/// <summary>
/// The most recent view of whether the machine can actually be streamed right now.
/// 0jay-style: two local endpoints must answer; the state is re-probed every few seconds.
/// </summary>
public sealed record SunshineLivenessSnapshot(
    bool Ready,
    string? Reason,
    DateTimeOffset ProbedAtUtc,
    int MoonlightPort,
    int SunshinePort)
{
    public static SunshineLivenessSnapshot Unknown { get; } =
        new(false, SunshineLivenessProbe.ReasonInvalidPort, DateTimeOffset.MinValue, 0, 0);
}

public interface ISunshineLiveness
{
    SunshineLivenessSnapshot Current { get; }
    Task<SunshineLivenessSnapshot> ProbeOnceAsync(CancellationToken ct);
}

public sealed class SunshineLivenessMonitor(
    ISunshineLivenessProbe probe,
    IOptions<AgentOptions> options,
    AppliedConfiguration configuration,
    ILogger<SunshineLivenessMonitor> logger,
    TimeProvider? time = null) : BackgroundService, ISunshineLiveness
{
    public const int NotReadyAfterMisses = 3;
    public static readonly TimeSpan Interval = TimeSpan.FromSeconds(5);

    private readonly TimeProvider clock = time ?? TimeProvider.System;
    private readonly SemaphoreSlim serial = new(1, 1);
    private SunshineLivenessSnapshot current = SunshineLivenessSnapshot.Unknown;
    private int consecutiveMisses;

    public SunshineLivenessSnapshot Current => Volatile.Read(ref current);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Probe immediately so readiness does not wait for the first timer tick.
        await ProbeSafelyAsync(stoppingToken);
        using var timer = new PeriodicTimer(Interval);
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
                await ProbeSafelyAsync(stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }

    private async Task ProbeSafelyAsync(CancellationToken ct)
    {
        try
        {
            await ProbeOnceAsync(ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            logger.LogWarning("Sunshine liveness probe failed ({ErrorType}).", ex.GetType().Name);
        }
    }

    public async Task<SunshineLivenessSnapshot> ProbeOnceAsync(CancellationToken ct)
    {
        await serial.WaitAsync(ct);
        try
        {
            var moonlightPort = configuration.Current?.MoonlightPort ?? options.Value.MoonlightPort;
            var sunshinePort = configuration.Current?.SunshinePort ?? options.Value.SunshinePort;
            SunshineProbeResult result;
            try
            {
                result = await probe.ProbeAsync(moonlightPort, sunshinePort, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                logger.LogWarning("Sunshine liveness probe threw ({ErrorType}).", ex.GetType().Name);
                result = new(false, false, SunshineLivenessProbe.ReasonWebUiSilent);
            }

            var now = clock.GetUtcNow();
            var ready = result.Ready;
            if (ready)
            {
                consecutiveMisses = 0;
            }
            else
            {
                consecutiveMisses = Math.Min(consecutiveMisses + 1, NotReadyAfterMisses);
                // Keep reporting ready until the hysteresis threshold is crossed; one
                // missed probe is not a reason to take a working machine off the shelf.
                if (consecutiveMisses < NotReadyAfterMisses && current.Ready)
                    ready = true;
            }

            var next = new SunshineLivenessSnapshot(ready, ready ? SunshineLivenessProbe.ReasonReady : result.Reason, now, moonlightPort, sunshinePort);
            Volatile.Write(ref current, next);
            return next;
        }
        finally
        {
            serial.Release();
        }
    }
}

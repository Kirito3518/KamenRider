using KamenRaider.Contracts;

namespace KamenRaider.Agent.Parsec;

/// <summary>
/// Delivers one Parsec attempt status to the Controller.
/// </summary>
/// <remarks>
/// Ported from 0jay-v2 <c>agent/parsec_manager.go</c>'s <c>parsecStatusReporter</c>. The manager
/// calls it from background work only; an implementation must never throw, because a delivery
/// problem must not take the desktop attempt (or the Agent service) down. It returns
/// <c>false</c> when the Controller did not accept the report — usually because the attempt is no
/// longer current, in which case the manager skips the login the renter already abandoned.
/// </remarks>
public interface IParsecStatusReporter
{
    Task<bool> ReportAsync(ParsecStatusReport report, CancellationToken cancellationToken);
}

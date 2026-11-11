using KamenRaider.Contracts;

namespace KamenRaider.Controller.Data;

public sealed record SyncDecision(int StatusCode, SyncResponse? Response = null, string? Challenge = null)
{
    public static SyncDecision Accepted(SyncResponse response) => new(200, response);
    public static SyncDecision ChallengeRequired(string challenge) => new(428, Challenge: challenge);
    public static SyncDecision Rejected(int statusCode) => new(statusCode);
}

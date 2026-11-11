namespace KamenRaider.Controller.Data;

public enum AgentReleaseUnavailableKind
{
    IntegrityMismatch,
    InvalidArchive,
    UnsupportedProtocolOrRuntime,
    StorageUnavailable,
}

public sealed class AgentReleaseUnavailableException : InvalidOperationException
{
    public const string ResponseCode = "release_unavailable";

    public AgentReleaseUnavailableException(AgentReleaseUnavailableKind kind, Exception? innerException = null)
        : base("The selected Agent release is unavailable.", innerException) => Kind = kind;

    public AgentReleaseUnavailableKind Kind { get; }

    public string SafeReason => Kind switch
    {
        AgentReleaseUnavailableKind.IntegrityMismatch => "Stored package does not match its published SHA-256.",
        AgentReleaseUnavailableKind.InvalidArchive => "Stored package failed ZIP and release metadata validation.",
        AgentReleaseUnavailableKind.UnsupportedProtocolOrRuntime => "Release uses an unsupported update protocol or runtime.",
        AgentReleaseUnavailableKind.StorageUnavailable => "Stored package could not be read from release storage.",
        _ => "Stored package is unavailable.",
    };
}

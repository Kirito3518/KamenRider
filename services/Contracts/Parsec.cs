namespace KamenRaider.Contracts;

/// <summary>
/// Wire contracts for the renter-supplied Parsec account login. The browser encrypts the
/// credentials with the Agent's boot public key; the Controller only relays ciphertext and
/// the Agent decrypts it in memory.
/// </summary>
public static class ParsecStates
{
    public const string Queued = "queued";
    public const string Starting = "starting";
    public const string WaitingEmail = "waiting_email";
    public const string Ready = "ready";
    public const string Failed = "failed";
}

public static class ParsecErrorCodes
{
    public const string InvalidCredentials = "INVALID_CREDENTIALS";
    public const string CaptchaRequired = "CAPTCHA_REQUIRED";
    public const string AppNotFound = "APP_NOT_FOUND";
    public const string WindowUnavailable = "WINDOW_UNAVAILABLE";
    public const string HostNotReady = "HOST_NOT_READY";
    public const string Timeout = "TIMEOUT";
    public const string AgentOffline = "AGENT_OFFLINE";
    public const string RateLimited = "RATE_LIMITED";
    public const string AccountLocked = "ACCOUNT_LOCKED";
    public const string ServiceUnavailable = "SERVICE_UNAVAILABLE";
    public const string NetworkError = "NETWORK_ERROR";
    public const string SessionConflict = "SESSION_CONFLICT";
    public const string PasswordResetRequired = "PASSWORD_RESET_REQUIRED";
    public const string AppUpdateRequired = "APP_UPDATE_REQUIRED";
    public const string TwoFactorUnsupported = "TWO_FACTOR_UNSUPPORTED";
    public const string Busy = "PARSEC_BUSY";
    public const string LoginUnconfirmed = "LOGIN_UNCONFIRMED";
    public const string Internal = "INTERNAL";
    public const string LeaseEnded = "LEASE_ENDED";
    public const string AgentRebooted = "AGENT_REBOOTED";
    /// <summary>The renter confirmed Parsec's e-mail/IP challenge and asked for a fresh login round.</summary>
    public const string EmailVerified = "EMAIL_VERIFIED";
}

/// <summary>The Agent's boot-only RSA public key, advertised in the signed heartbeat.</summary>
public sealed record ParsecCapability(string BootKeyId, string PublicKey, bool Available);

public sealed record ParsecSealedSecret(string WrappedKey, string Iv, string Ciphertext);

/// <summary>
/// A renter login request. <paramref name="RetryAttemptId"/> is set only for round two of the
/// e-mail/IP challenge: the named attempt must currently be <c>waiting_email</c> for the same
/// lease, renter and boot key. It is optional so every first-round caller keeps working.
/// </summary>
public sealed record ParsecAttemptRequest(
    string UserId,
    string IdempotencyKey,
    ParsecSealedSecret Secret,
    string? RetryAttemptId = null);

public sealed record ParsecAttemptDto(
    string AttemptId,
    string State,
    string? ErrorCode,
    string? Message,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc,
    DateTimeOffset ExpiresAtUtc,
    string? BootKeyId);

/// <summary>Signed status report from the Agent; message is short, sanitized Thai/English text.</summary>
public sealed record ParsecStatusReport(string AttemptId, string BootKeyId, string Status, string? ErrorCode, string? Message);

/// <summary>The last capability the Agent advertised, as stored by the Controller.</summary>
public sealed record MachineParsecCapability(bool Available, string? BootKeyId, string? PublicKey)
{
    public static MachineParsecCapability Unavailable { get; } = new(false, null, null);
}

/// <summary>What the renter's browser may do with Parsec on this lease's machine.</summary>
public sealed record ParsecLeaseState(
    bool Available,
    string? BootKeyId,
    string? PublicKey,
    ParsecAttemptDto? Attempt);

/// <summary>Admin per-machine Parsec opt-in.</summary>
public sealed record MachineParsecToggle(bool Enabled);



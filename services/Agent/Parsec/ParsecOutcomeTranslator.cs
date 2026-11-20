using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using KamenRaider.Contracts;

namespace KamenRaider.Agent.Parsec;

/// <summary>The outcome vocabulary agent-parsec used for a translated notification.</summary>
public static class ParsecOutcomes
{
    public const string Success = "success";
    public const string Failed = "failed";
    public const string RequiresAction = "requires_action";
    public const string Unknown = "unknown";
}

/// <summary>
/// agent-parsec's internal banner statuses. They are mapped onto the wire
/// <see cref="ParsecStates"/>/<see cref="ParsecErrorCodes"/> by
/// <see cref="ParsecOutcomeTranslator.LoginOutcome"/> and never leave the Agent.
/// </summary>
public static class ParsecBannerStatuses
{
    public const string Success = "SUCCESS";
    public const string EmailVerificationRequired = "EMAIL_VERIFICATION_REQUIRED";
    public const string DeviceAuthorizationRequired = "DEVICE_AUTHORIZATION_REQUIRED";
    public const string TwoFactorRequired = "TWO_FACTOR_REQUIRED";
    public const string InvalidCredentials = "INVALID_CREDENTIALS";
    public const string InvalidEmail = "INVALID_EMAIL";
    public const string BadRequest = "BAD_REQUEST";
    public const string NotFound = "NOT_FOUND";
    public const string CaptchaRequired = "CAPTCHA_REQUIRED";
    public const string RateLimited = "RATE_LIMITED";
    public const string AccountLocked = "ACCOUNT_LOCKED";
    public const string ServiceUnavailable = "SERVICE_UNAVAILABLE";
    public const string NetworkError = "NETWORK_ERROR";
    public const string SessionConflict = "SESSION_CONFLICT";
    public const string PasswordResetRequired = "PASSWORD_RESET_REQUIRED";
    public const string UpdateRequired = "UPDATE_REQUIRED";
    public const string NoNotification = "NO_NOTIFICATION";
    public const string UnknownAlert = "UNKNOWN_ALERT";
}

/// <summary>One of agent-parsec's translated banner rules; all keywords must appear as substrings.</summary>
public sealed class ParsecTranslateRule
{
    public string[] Keywords { get; init; } = Array.Empty<string>();
    public string Thai { get; init; } = string.Empty;
    public string Outcome { get; init; } = ParsecOutcomes.Unknown;
    public string Status { get; init; } = ParsecBannerStatuses.NoNotification;
}

/// <summary>One of agent-parsec's known Parsec error-code translations (400, 401, 429, ...).</summary>
public sealed class ParsecCodeTranslation
{
    public string Code { get; init; } = string.Empty;
    public string Thai { get; init; } = string.Empty;
    public string Outcome { get; init; } = ParsecOutcomes.Unknown;
    public string Status { get; init; } = ParsecBannerStatuses.NoNotification;
}

/// <summary>One of agent-parsec's word-by-word fallbacks, tried in order for unknown banners.</summary>
public sealed class ParsecFallbackTranslation
{
    private Regex? compiled;

    public string Pattern { get; init; } = string.Empty;
    public string Replacement { get; init; } = string.Empty;

    /// <summary>The compiled pattern; the catalog loads it eagerly to reject a bad table at boot.</summary>
    internal Regex Compiled => compiled ??= Compile(Pattern);

    private static Regex Compile(string pattern)
    {
        try
        {
            return new Regex(pattern, RegexOptions.Compiled | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(2));
        }
        catch (ArgumentException exception)
        {
            throw new InvalidOperationException($"The embedded Parsec translation pattern '{pattern}' is not a valid regular expression.", exception);
        }
    }
}

/// <summary>
/// A Go-to-C# port of the owner's LordX3d agent-parsec <c>lib/parsec-translate.js</c>, plus the
/// part of its <c>agent.js</c> that picks the best of the banner frames and decides whether the
/// login worked (0jay-v2 <c>agent/parsec_translate.go</c>).
/// </summary>
/// <remarks>
/// The desktop script photographs the strip across the top of the screen where Parsec shows the
/// result of a login six times, about two seconds apart, and reads each frame with Windows OCR.
/// Everything from there on happens here: clean the OCR text down to the notification lines,
/// match it against the known messages (all keywords present as substrings, because OCR of real
/// UI text is noisy), and fall back to a word-by-word Thai translation of anything unknown.
///
/// The tables are LordX3d's, in LordX3d's order, loaded from the embedded
/// <c>parsec-translate.json</c>. Only the way the result is reported differs: the final mapping
/// produces this system's <see cref="ParsecStates"/> and <see cref="ParsecErrorCodes"/>.
///
/// Nothing here ever returns the raw OCR text to the caller for forwarding: the notification
/// text is trimmed to four lines and the message built from it is capped at
/// <see cref="MessageLimit"/> and stripped of e-mail addresses and IP addresses, so Parsec's
/// words are shown without the renter's account details or the shop's public address.
/// </remarks>
public static class ParsecOutcomeTranslator
{
    public const int DefaultMessageLimit = 200;

    /// <summary>"No notification was found" — the exact Thai text agent-parsec used.</summary>
    public const string NoNotificationText = "ไม่พบข้อความแจ้งเตือนจาก Parsec";

    /// <summary>Fallback when a notification was found but nothing in it could be translated.</summary>
    public const string EmptyBannerText = "ไม่พบข้อความในแถบแจ้งเตือนของ Parsec";

    /// <summary>login failed without any banner: Say what the renter should look for.</summary>
    public const string LoginUnconfirmedText = "ไม่พบข้อความแจ้งเตือนในแถบด้านบนของ Parsec หลังคลิก Login";

    internal const string AllBannerTextPrefix = "ข้อความแจ้งเตือนทั้งหมด: ";
    internal const string CodeMetadataPrefix = "รหัส ";
    internal const string IpMetadataPrefix = "IP ";
    internal const string MetadataSeparator = " · ";

    private const string RuntimeAppNotFound = "APP_NOT_FOUND";
    private const string RuntimeAppWindowNotFound = "APP_WINDOW_NOT_FOUND";
    private const string RuntimeTimeout = "TIMEOUT";

    private static readonly ParsecTranslationCatalog Catalog = ParsecTranslationCatalog.Load();

    private static readonly Regex NotificationSignal = new(
        @"(?i)\b(?:error|warning|notice|alert|email|verify|confirm|incorrect|invalid|password|captcha|factor|network|connection|unavailable|session|attempt|login|log\s*in|account|unauthorized|denied|failed|successfully|successful|required|locked|disabled|suspended|banned)\b|\[\s*\d{3}\s*\]",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex[] OcrNoise =
    {
        new(@"(?i)^\s*\d+\)\s*[^\r\n]*#\d{5,}\s*$", RegexOptions.Compiled | RegexOptions.CultureInvariant),
        new(@"(?i)\balt\s*\+\s*z\b", RegexOptions.Compiled | RegexOptions.CultureInvariant),
        new(@"(?i)\bnvidia\b", RegexOptions.Compiled | RegexOptions.CultureInvariant),
        new(@"(?i)^\s*(?:computers?|friends?|settings?|help|teams?)\s*$", RegexOptions.Compiled | RegexOptions.CultureInvariant)
    };

    // Only an explicit "Error 403" or "[403]" is an error code, never three digits inside a
    // username such as #16676236.
    private static readonly Regex ErrorCodePattern = new(
        @"(?i)\berror\s*(?:code\s*)?[\[\(:#-]?\s*(\d{3})\s*\]?",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex BracketCodePattern = new(
        @"(?i)\[\s*(\d{3})\s*\]",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex IpPattern = new(
        @"\b(?:\d{1,3}\.){3}\d{1,3}\b",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex LineBreak = new(@"\r?\n", RegexOptions.Compiled);
    private static readonly Regex HorizontalSpace = new(@"[ \t]+", RegexOptions.Compiled);

    private static readonly Regex EmailAddress = new(
        @"(?i)[a-z0-9._%+\-]+@[a-z0-9.\-]+\.[a-z]{2,}",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex IpAny = new(
        @"(?:\d{1,3}\.){3}\d{1,3}|\b(?:[0-9a-f]{1,4}:){2,7}[0-9a-f]{1,4}\b",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    private static readonly Regex IpLabel = new(
        @"(?i)\s*·?\s*\bIP\b\s*(?:·|$)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex EmptyBrackets = new(
        @"\(\s*\)|\[\s*\]",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex RepeatedSpaces = new(@"[ \t]{2,}", RegexOptions.Compiled);
    private static readonly Regex NotificationId = new(@"^[0-9]{3}$", RegexOptions.Compiled);

    private static readonly Regex AppMissingError = new(
        @"(?i)APP_NOT_FOUND|ไม่พบ.*parsecd\.exe|parsec app.*not found",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex WindowMissingError = new(
        @"(?i)APP_WINDOW_NOT_FOUND|WINDOW_FOCUS_LOST|ไม่พบหน้าต่าง parsec|window.*not found",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex TimeoutError = new(
        @"(?i)timed out|timeout|เกินเวลา",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>The maximum number of characters a report message may carry.</summary>
    public static int MessageLimit => Catalog.MessageLimit;

    /// <summary>Prefix used when an unrecognised banner has no word-by-word translation.</summary>
    public static string UnknownBannerPrefix => Catalog.UnknownBannerPrefix;

    /// <summary>The happy-path text agent-parsec showed when no banner confirmed the login.</summary>
    public static string LoginSucceededText => Catalog.LoginSucceededText;

    /// <summary>Every translated rule, in agent-parsec's order.</summary>
    public static IReadOnlyList<ParsecTranslateRule> Rules => Catalog.Rules;

    /// <summary>The known Parsec HTTP-status translations.</summary>
    public static IReadOnlyList<ParsecCodeTranslation> CodeTranslations => Catalog.Codes;

    /// <summary>The word-by-word fallbacks, applied in order to an unknown banner.</summary>
    public static IReadOnlyList<ParsecFallbackTranslation> Fallbacks => Catalog.Fallbacks;

    /// <summary>True when every keyword appears in <paramref name="text"/>, case-insensitively.</summary>
    public static bool ContainsAll(string text, IReadOnlyList<string> keywords)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(keywords);
        foreach (var keyword in keywords)
            if (!text.Contains(keyword, StringComparison.Ordinal))
                return false;
        return true;
    }

    /// <summary>True when the OCR line is a known non-notification overlay, account chip or nav item.</summary>
    public static bool IsOcrNoise(string line)
    {
        ArgumentNullException.ThrowIfNull(line);
        foreach (var pattern in OcrNoise)
            if (pattern.IsMatch(line))
                return true;
        return false;
    }

    /// <summary>
    /// Keeps at most four lines from the first one that looks like a notification: account names,
    /// overlays and the Computers page that OCR may also see are dropped.
    /// </summary>
    public static string CleanNotificationText(string rawText)
    {
        ArgumentNullException.ThrowIfNull(rawText);
        var lines = new List<string>();
        foreach (var rawLine in LineBreak.Split(rawText.Replace("\0", string.Empty, StringComparison.Ordinal)))
        {
            var line = HorizontalSpace.Replace(rawLine, " ").Trim();
            if (line.Length == 0 || IsOcrNoise(line))
                continue;
            lines.Add(line);
        }
        for (var index = 0; index < lines.Count; index++)
        {
            if (!NotificationSignal.IsMatch(lines[index]))
                continue;
            var count = Math.Min(4, lines.Count - index);
            return string.Join("\n", lines.GetRange(index, count)).Trim();
        }
        return string.Empty;
    }

    /// <summary>Word-by-word Thai translation of a banner that matched no known rule.</summary>
    public static string TranslateUnknownBanner(string rawText)
    {
        ArgumentNullException.ThrowIfNull(rawText);
        var translated = rawText.Trim();
        var before = translated;
        foreach (var fallback in Catalog.Fallbacks)
            translated = fallback.Compiled.Replace(translated, _ => fallback.Replacement);
        if (translated.Length == 0)
            return EmptyBannerText;
        if (string.Equals(translated, before, StringComparison.Ordinal))
            return UnknownBannerPrefix + " " + before;
        return translated;
    }

    /// <summary>
    /// LordX3d's translateParsecNotification: clean the OCR text, extract a known error code and
    /// IP address, then match the rules, the code table and the fallbacks in that order.
    /// </summary>
    public static ParsecTranslation TranslateNotification(string? rawText)
    {
        var notificationText = CleanNotificationText(rawText ?? string.Empty);
        var normalized = notificationText.ToLowerInvariant();
        var code = string.Empty;
        var codeMatch = ErrorCodePattern.Match(normalized);
        if (codeMatch.Success)
            code = codeMatch.Groups[1].Value;
        else
        {
            var bracketMatch = BracketCodePattern.Match(normalized);
            if (bracketMatch.Success)
                code = bracketMatch.Groups[1].Value;
        }
        var ipMatch = IpPattern.Match(normalized);
        var ip = ipMatch.Success ? ipMatch.Value : string.Empty;

        if (notificationText.Length == 0)
        {
            return new ParsecTranslation
            {
                TranslatedText = NoNotificationText,
                Outcome = ParsecOutcomes.Unknown,
                Status = ParsecBannerStatuses.NoNotification
            };
        }

        var result = new ParsecTranslation
        {
            Matched = true,
            TranslationComplete = true,
            NotificationCode = code,
            NotificationIp = ip,
            NotificationText = notificationText,
            NotificationDetected = true
        };

        // Confirmed by the owner from a real Parsec 150-104 banner: Error [403] means an e-mail
        // was sent to approve this login attempt from this IP address.
        if (code == "403")
        {
            var translated = "ข้อผิดพลาด [403]\nParsec ส่งอีเมลไปยังที่อยู่อีเมลของบัญชีแล้ว เพื่อยืนยันความพยายามเข้าสู่ระบบจากที่อยู่ IP นี้";
            if (ip.Length > 0)
                translated += " (" + ip + ")";
            translated += " กรุณาเปิดอีเมลและกดยืนยันการเข้าสู่ระบบ";
            return result with
            {
                TranslatedText = translated,
                Outcome = ParsecOutcomes.RequiresAction,
                Status = ParsecBannerStatuses.EmailVerificationRequired
            };
        }

        if (code.Length > 0 && Catalog.CodeLookup.TryGetValue(code, out var codeTranslation))
        {
            return result with
            {
                TranslatedText = IncludeNotificationMetadata(codeTranslation.Thai, code, ip),
                Outcome = codeTranslation.Outcome,
                Status = codeTranslation.Status
            };
        }

        foreach (var rule in Catalog.Rules)
        {
            if (!ContainsAll(normalized, rule.Keywords))
                continue;
            var translated = rule.Thai;
            if (rule.Outcome != ParsecOutcomes.Success)
            {
                var full = TranslateUnknownBanner(notificationText);
                if (full.Length > 0 && !full.StartsWith(UnknownBannerPrefix, StringComparison.Ordinal))
                    translated = rule.Thai + "\n" + AllBannerTextPrefix + full;
            }
            return result with
            {
                TranslatedText = IncludeNotificationMetadata(translated, code, ip),
                Outcome = rule.Outcome,
                Status = rule.Status
            };
        }

        result = result with
        {
            TranslatedText = IncludeNotificationMetadata(TranslateUnknownBanner(notificationText), code, ip),
            Matched = false,
            TranslationComplete = false,
            Outcome = ParsecOutcomes.Unknown,
            Status = ParsecBannerStatuses.UnknownAlert
        };
        return result;
    }

    /// <summary>
    /// agent.js's /parsec-login result assembly: score every frame (a matched rule 10000, a
    /// notification 1000, an error code 500, plus up to 400 for the text's length), stop at the
    /// first frame that settles it, and call the login a success only when the script finished,
    /// no frame says it failed or needs action, and a session was found.
    /// </summary>
    public static ParsecLoginResult EvaluateLogin(ParsecScriptStatus status)
    {
        ArgumentNullException.ThrowIfNull(status);
        ParsecTranslation? best = null;
        var bestScore = -1;
        foreach (var frame in status.Frames ?? Array.Empty<string>())
        {
            var translation = TranslateNotification(frame);
            var score = 0;
            if (translation.Matched) score += 10_000;
            if (translation.NotificationDetected) score += 1_000;
            if (translation.NotificationCode.Length > 0) score += 500;
            score += Math.Min(400, translation.NotificationText.Length);
            if (best is null || score > bestScore)
            {
                best = translation;
                bestScore = score;
            }
            if (translation.Matched &&
                (translation.NotificationCode.Length > 0 || translation.Outcome != ParsecOutcomes.Unknown))
                break;
        }

        var notificationDetected = best is not null && best.NotificationDetected && best.NotificationText.Length > 0;
        var selected = best ?? TranslateNotification(string.Empty);
        var success = status.AutomationCompleted &&
                      selected.Outcome != ParsecOutcomes.Failed &&
                      selected.Outcome != ParsecOutcomes.RequiresAction &&
                      (status.SessionFound || status.LoginFormGone);

        var result = new ParsecLoginResult
        {
            Success = success,
            Status = selected.Status,
            Outcome = selected.Outcome,
            TranslatedText = selected.TranslatedText,
            NotificationCode = selected.NotificationCode,
            NotificationDetected = notificationDetected,
            SessionFound = status.SessionFound,
            Error = status.Error ?? string.Empty
        };

        if (success)
        {
            result = result with
            {
                Status = ParsecBannerStatuses.Success,
                Outcome = ParsecOutcomes.Success
            };
            if (!notificationDetected)
            {
                result = result with
                {
                    TranslatedText = LoginSucceededText,
                    NotificationCode = string.Empty
                };
            }
            return result;
        }

        if (result.Error.Length == 0)
            result = result with { Error = FirstNonEmpty(result.TranslatedText, LoginUnconfirmedText) };
        return result;
    }

    /// <summary>Turns the script's JSON status into what the web is told, as agent-parsec did.</summary>
    public static ParsecReport ScriptOutcome(ParsecScriptStatus status)
    {
        ArgumentNullException.ThrowIfNull(status);
        if (!status.AutomationCompleted && !string.IsNullOrWhiteSpace(status.Error))
            return RuntimeFailure(status.Error);
        return LoginOutcome(EvaluateLogin(status));
    }

    /// <summary>
    /// Maps LordX3d's answer onto this system's states. READY starts the rental clock, so it is
    /// only ever LordX3d's own success (the script finished, nothing failed and a session exists
    /// on disk). An e-mailed approval is WAITING_EMAIL; the renter approves it and logs in again.
    /// There is no 2FA step in LordX3d, so a code request ends the attempt with
    /// TWO_FACTOR_UNSUPPORTED.
    /// </summary>
    public static ParsecReport LoginOutcome(ParsecLoginResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        var report = new ParsecReport(
            Status: result.Success ? ParsecStates.Ready : ParsecStates.Failed,
            Message: SanitizeMessage(result.TranslatedText),
            NotificationCode: SanitizeNotificationCode(result.NotificationCode));
        if (result.Success)
            return report;

        return result.Status switch
        {
            ParsecBannerStatuses.EmailVerificationRequired or ParsecBannerStatuses.DeviceAuthorizationRequired =>
                report with { Status = ParsecStates.WaitingEmail },
            ParsecBannerStatuses.TwoFactorRequired =>
                report with { ErrorCode = ParsecErrorCodes.TwoFactorUnsupported },
            ParsecBannerStatuses.InvalidCredentials or ParsecBannerStatuses.InvalidEmail or
                ParsecBannerStatuses.BadRequest or ParsecBannerStatuses.NotFound =>
                report with { ErrorCode = ParsecErrorCodes.InvalidCredentials },
            ParsecBannerStatuses.CaptchaRequired =>
                report with { ErrorCode = ParsecErrorCodes.CaptchaRequired },
            ParsecBannerStatuses.RateLimited =>
                report with { ErrorCode = ParsecErrorCodes.RateLimited },
            ParsecBannerStatuses.AccountLocked =>
                report with { ErrorCode = ParsecErrorCodes.AccountLocked },
            ParsecBannerStatuses.ServiceUnavailable =>
                report with { ErrorCode = ParsecErrorCodes.ServiceUnavailable },
            ParsecBannerStatuses.NetworkError =>
                report with { ErrorCode = ParsecErrorCodes.NetworkError },
            ParsecBannerStatuses.SessionConflict =>
                report with { ErrorCode = ParsecErrorCodes.SessionConflict },
            ParsecBannerStatuses.PasswordResetRequired =>
                report with { ErrorCode = ParsecErrorCodes.PasswordResetRequired },
            ParsecBannerStatuses.UpdateRequired =>
                report with { ErrorCode = ParsecErrorCodes.AppUpdateRequired },
            // NO_NOTIFICATION, UNKNOWN_ALERT, or a success banner with no session on disk:
            // Parsec never confirmed the login.
            _ => report with { ErrorCode = ParsecErrorCodes.LoginUnconfirmed }
        };
    }

    /// <summary>
    /// Maps a script that did not finish (agent-parsec answered these with HTTP 400 and
    /// classifyParsecRuntimeError's status).
    /// </summary>
    public static ParsecReport RuntimeFailure(string? message)
    {
        var report = new ParsecReport(ParsecStates.Failed, Message: SanitizeMessage(message ?? string.Empty));
        return ClassifyRuntimeError(message ?? string.Empty) switch
        {
            RuntimeAppNotFound => report with
            {
                ErrorCode = ParsecErrorCodes.AppNotFound,
                Message = "ไม่พบ C:\\Program Files\\Parsec\\parsecd.exe"
            },
            RuntimeAppWindowNotFound => report with
            {
                ErrorCode = ParsecErrorCodes.WindowUnavailable,
                Message = message is not null && message.Contains("WINDOW_FOCUS_LOST", StringComparison.Ordinal)
                    ? "หน้าต่าง Parsec ไม่ได้อยู่ด้านหน้าระหว่างกรอกข้อมูล ระบบหยุดพิมพ์เพื่อความปลอดภัย"
                    : "สั่งเปิด parsecd.exe แล้ว แต่ไม่พบหน้าต่าง Parsec ใน desktop session ภายใน 35 วินาที"
            },
            RuntimeTimeout => report with { ErrorCode = ParsecErrorCodes.Timeout },
            _ => report with { ErrorCode = ParsecErrorCodes.HostNotReady }
        };
    }

    /// <summary>
    /// Maps a desktop script that never reported. The Agent reports TIMEOUT for a deadline and
    /// HOST_NOT_READY for "no desktop user" or "the task could not be started", exactly as
    /// 0jay-v2's parsecInteractiveFailure did, so the web can tell the renter to look at the
    /// screen instead of retrying a password.
    /// </summary>
    public static ParsecReport InteractiveFailure(Exception error)
    {
        ArgumentNullException.ThrowIfNull(error);
        return error switch
        {
            ParsecDesktopUnavailableException => new ParsecReport(
                ParsecStates.Failed,
                ParsecErrorCodes.HostNotReady,
                "ไม่พบ user ที่ login อยู่บนเครื่องนี้ในขณะนี้"),
            ParsecScriptTimeoutException or OperationCanceledException => new ParsecReport(
                ParsecStates.Failed,
                ParsecErrorCodes.Timeout,
                "หมดเวลารอผลลัพธ์จากเครื่อง"),
            _ => new ParsecReport(
                ParsecStates.Failed,
                ParsecErrorCodes.HostNotReady,
                "สั่งงานบนหน้าจอเครื่องไม่สำเร็จ")
        };
    }

    /// <summary>LordX3d's classifyParsecRuntimeError; the script throws the ASCII codes.</summary>
    public static string ClassifyRuntimeError(string message)
    {
        ArgumentNullException.ThrowIfNull(message);
        if (AppMissingError.IsMatch(message)) return RuntimeAppNotFound;
        if (WindowMissingError.IsMatch(message)) return RuntimeAppWindowNotFound;
        if (TimeoutError.IsMatch(message)) return RuntimeTimeout;
        return "ERROR";
    }

    /// <summary>
    /// Decodes the one-line JSON status the desktop scripts write, with a leading UTF-8 BOM
    /// tolerated exactly as agent-parsec's status file reader did.
    /// </summary>
    /// <exception cref="FormatException">The status is not the expected JSON object.</exception>
    public static ParsecScriptStatus DecodeScriptStatus(ReadOnlySpan<byte> content)
    {
        ReadOnlySpan<byte> body = content;
        if (body.Length >= 3 && body[0] == 0xEF && body[1] == 0xBB && body[2] == 0xBF)
            body = body[3..];
        try
        {
            var status = JsonSerializer.Deserialize(body, ParsecJsonContext.Default.ParsecScriptStatus);
            return status ?? throw new FormatException("The Parsec desktop script status is empty.");
        }
        catch (JsonException)
        {
            // No inner exception: a JSON error can name a property path, and the status carries
            // the OCR text of the banner strip, which never belongs in a log.
            throw new FormatException("The Parsec desktop script status is not a JSON object.");
        }
    }

    /// <summary>
    /// Prepares LordX3d's Thai text for the web: no e-mail address and no IP address (Parsec
    /// names the IP the login came from), no empty brackets left behind, at most
    /// <see cref="MessageLimit"/> characters. OCR text itself is never reportable either; only
    /// events built from the tables above are.
    /// </summary>
    public static string SanitizeMessage(string? message)
    {
        if (string.IsNullOrEmpty(message))
            return string.Empty;
        var text = EmailAddress.Replace(message, string.Empty);
        text = IpAny.Replace(text, string.Empty);

        var lines = text.Split('\n');
        var kept = new List<string>(lines.Length);
        foreach (var rawLine in lines)
        {
            var line = EmptyBrackets.Replace(rawLine, string.Empty);
            line = IpLabel.Replace(line, string.Empty);
            line = RepeatedSpaces.Replace(line, " ").Trim(' ', '\t', '·');
            if (line.Length > 0)
                kept.Add(line);
        }

        return TruncateRunes(string.Join("\n", kept), MessageLimit);
    }

    /// <summary>A Parsec error code may be reported only as exactly three digits.</summary>
    public static string SanitizeNotificationCode(string? code) =>
        code is not null && NotificationId.IsMatch(code) ? code : string.Empty;

    private static string IncludeNotificationMetadata(string text, string code, string ip)
    {
        var metadata = new List<string>(2);
        if (code.Length > 0)
            metadata.Add(CodeMetadataPrefix + code);
        if (ip.Length > 0)
            metadata.Add(IpMetadataPrefix + ip);
        return metadata.Count == 0 ? text : text + "\n" + string.Join(MetadataSeparator, metadata);
    }

    private static string FirstNonEmpty(string first, string second) => first.Length > 0 ? first : second;

    private static string TruncateRunes(string value, int limit)
    {
        var count = 0;
        var lengthInChars = 0;
        foreach (var rune in value.EnumerateRunes())
        {
            if (count == limit)
                return value[..lengthInChars].Trim();
            count++;
            lengthInChars += rune.Utf16SequenceLength;
        }
        return value;
    }
}

/// <summary>
/// The JSON the login script writes when it finishes: the same fields as agent-parsec's status
/// file, with the OCR text of each banner frame in place of PNG files (the frames never leave
/// the script's memory).
/// </summary>
public sealed record ParsecScriptStatus
{
    public bool AutomationCompleted { get; init; }
    public bool SessionFound { get; init; }
    public bool LoginFormGone { get; init; }
    public string? Method { get; init; }
    public string? Error { get; init; }
    public string[]? Frames { get; init; }
    public int OcrErrors { get; init; }
}

/// <summary>One translated banner; fields mirror agent-parsec's translateParsecNotification.</summary>
public sealed record ParsecTranslation
{
    public string TranslatedText { get; init; } = string.Empty;
    public bool Matched { get; init; }
    public bool TranslationComplete { get; init; }
    public string Outcome { get; init; } = ParsecOutcomes.Unknown;
    public string Status { get; init; } = ParsecBannerStatuses.NoNotification;
    public string NotificationCode { get; init; } = string.Empty;
    public string NotificationIp { get; init; } = string.Empty;
    public string NotificationText { get; init; } = string.Empty;
    public bool NotificationDetected { get; init; }
}

/// <summary>agent-parsec's /parsec-login result, minus the screenshot and the raw OCR text.</summary>
public sealed record ParsecLoginResult
{
    public bool Success { get; init; }
    public string Status { get; init; } = ParsecBannerStatuses.NoNotification;
    public string Outcome { get; init; } = ParsecOutcomes.Unknown;
    public string TranslatedText { get; init; } = string.Empty;
    public string NotificationCode { get; init; } = string.Empty;
    public bool NotificationDetected { get; init; }
    public bool SessionFound { get; init; }
    public string Error { get; init; } = string.Empty;
}

/// <summary>
/// What one Parsec attempt reports: a wire status (<see cref="ParsecStates"/>), an error code
/// when it failed (<see cref="ParsecErrorCodes"/>) and the short sanitised Thai/English message
/// agent-parsec would have shown, plus Parsec's own three-digit notification code when there was
/// one.
/// </summary>
public sealed record ParsecReport(
    string Status,
    string? ErrorCode = null,
    string? Message = null,
    string? NotificationCode = null);

/// <summary>The shape of the embedded <c>parsec-translate.json</c> translation table.</summary>
internal sealed class ParsecTranslationDocument
{
    public int MessageLimit { get; init; }
    public string UnknownBannerPrefix { get; init; } = string.Empty;
    public string LoginSucceededText { get; init; } = string.Empty;
    public ParsecTranslateRule[] Rules { get; init; } = Array.Empty<ParsecTranslateRule>();
    public ParsecCodeTranslation[] Codes { get; init; } = Array.Empty<ParsecCodeTranslation>();
    public ParsecFallbackTranslation[] Fallbacks { get; init; } = Array.Empty<ParsecFallbackTranslation>();
}

/// <summary>Loads and validates the embedded translation tables; one instance per process.</summary>
internal sealed class ParsecTranslationCatalog
{
    private ParsecTranslationCatalog(
        int messageLimit,
        string unknownBannerPrefix,
        string loginSucceededText,
        IReadOnlyList<ParsecTranslateRule> rules,
        IReadOnlyList<ParsecCodeTranslation> codes,
        IReadOnlyList<ParsecFallbackTranslation> fallbacks,
        IReadOnlyDictionary<string, ParsecCodeTranslation> codeLookup)
    {
        MessageLimit = messageLimit;
        UnknownBannerPrefix = unknownBannerPrefix;
        LoginSucceededText = loginSucceededText;
        Rules = rules;
        Codes = codes;
        Fallbacks = fallbacks;
        CodeLookup = codeLookup;
    }

    internal int MessageLimit { get; }
    internal string UnknownBannerPrefix { get; }
    internal string LoginSucceededText { get; }
    internal IReadOnlyList<ParsecTranslateRule> Rules { get; }
    internal IReadOnlyList<ParsecCodeTranslation> Codes { get; }
    internal IReadOnlyList<ParsecFallbackTranslation> Fallbacks { get; }
    internal IReadOnlyDictionary<string, ParsecCodeTranslation> CodeLookup { get; }

    internal static ParsecTranslationCatalog Load()
    {
        ParsecTranslationDocument document;
        try
        {
            document = JsonSerializer.Deserialize(
                ParsecResources.ReadText(ParsecResources.TranslateJson),
                ParsecJsonContext.Default.ParsecTranslationDocument)
                ?? throw new InvalidOperationException("The embedded Parsec translation table is empty.");
        }
        catch (JsonException exception)
        {
            throw new InvalidOperationException("The embedded Parsec translation table is not valid JSON.", exception);
        }

        if (document.MessageLimit is < 1 or > 2_000)
            throw new InvalidOperationException("The embedded Parsec message limit is outside 1..2000.");
        if (string.IsNullOrWhiteSpace(document.UnknownBannerPrefix) || string.IsNullOrWhiteSpace(document.LoginSucceededText))
            throw new InvalidOperationException("The embedded Parsec translation table is missing its texts.");
        if (document.Rules is not { Length: > 0 })
            throw new InvalidOperationException("The embedded Parsec translation table has no rules.");
        if (document.Codes is null)
            throw new InvalidOperationException("The embedded Parsec translation table has no code translations.");
        if (document.Fallbacks is null)
            throw new InvalidOperationException("The embedded Parsec translation table has no fallbacks.");

        foreach (var rule in document.Rules)
        {
            if (rule.Keywords is not { Length: > 0 } ||
                rule.Keywords.Any(keyword => string.IsNullOrWhiteSpace(keyword)) ||
                string.IsNullOrWhiteSpace(rule.Thai) ||
                string.IsNullOrWhiteSpace(rule.Status) ||
                !IsOutcome(rule.Outcome))
                throw new InvalidOperationException("The embedded Parsec translation table has an invalid rule.");
        }

        var lookup = new Dictionary<string, ParsecCodeTranslation>(StringComparer.Ordinal);
        foreach (var code in document.Codes)
        {
            if (code.Code is not { Length: 3 } ||
                !code.Code.All(character => character is >= '0' and <= '9') ||
                string.IsNullOrWhiteSpace(code.Thai) ||
                string.IsNullOrWhiteSpace(code.Status) ||
                !IsOutcome(code.Outcome) ||
                !lookup.TryAdd(code.Code, code))
                throw new InvalidOperationException("The embedded Parsec translation table has an invalid error-code translation.");
        }

        // Touch every fallback's lazily compiled pattern, so a bad pattern fails the load with a
        // clear InvalidOperationException instead of a per-attempt exception.
        foreach (var fallback in document.Fallbacks)
        {
            if (string.IsNullOrWhiteSpace(fallback.Pattern) || string.IsNullOrEmpty(fallback.Replacement))
                throw new InvalidOperationException("The embedded Parsec translation table has an invalid fallback.");
            _ = fallback.Compiled;
        }

        return new ParsecTranslationCatalog(
            document.MessageLimit,
            document.UnknownBannerPrefix,
            document.LoginSucceededText,
            Array.AsReadOnly(document.Rules),
            Array.AsReadOnly(document.Codes),
            Array.AsReadOnly(document.Fallbacks),
            lookup);
    }

    private static bool IsOutcome(string? outcome) =>
        outcome is ParsecOutcomes.Success or ParsecOutcomes.Failed or ParsecOutcomes.RequiresAction or ParsecOutcomes.Unknown;
}

using System.Text.Json;

namespace KamenRaider.Agent.Parsec;

/// <summary>A point in the reference screen coordinates of the Parsec login layout.</summary>
public readonly record struct ParsecPoint(int X, int Y);

/// <summary>A rectangle in the reference screen coordinates of the Parsec login layout.</summary>
public readonly record struct ParsecRegion(int X, int Y, int Width, int Height);

/// <summary>The bounds of the monitor the Parsec window is on, in physical screen coordinates.</summary>
public readonly record struct ParsecScreenBounds(int Left, int Top, int Width, int Height);

/// <summary>
/// Where Parsec's login form and notification banner are, from LordX3d agent-parsec's
/// <c>config.json</c> <c>parsecLogin</c> block: measured on a maximised Parsec window on a
/// 1920x1080 screen and scaled to the real screen exactly as the login script does.
/// </summary>
public sealed record ParsecLoginLayout(
    int ReferenceWidth,
    int ReferenceHeight,
    ParsecPoint EmailPos,
    ParsecPoint PasswordPos,
    ParsecPoint LoginButtonPos,
    ParsecRegion NotificationRegion)
{
    /// <summary>
    /// Whether every point and the banner region sit inside the reference screen, so a typo
    /// can never send clicks to half-default, half-garbage coordinates.
    /// </summary>
    public bool IsValid()
    {
        const int maximum = 16384;
        if (ReferenceWidth is < 320 or > maximum || ReferenceHeight is < 240 or > maximum)
            return false;
        static bool Inside(ParsecPoint point, int width, int height) =>
            point.X >= 0 && point.Y >= 0 && point.X < width && point.Y < height;
        var region = NotificationRegion;
        return Inside(EmailPos, ReferenceWidth, ReferenceHeight) &&
               Inside(PasswordPos, ReferenceWidth, ReferenceHeight) &&
               Inside(LoginButtonPos, ReferenceWidth, ReferenceHeight) &&
               region.X >= 0 && region.Y >= 0 && region.Width > 0 && region.Height > 0 &&
               region.X + region.Width <= ReferenceWidth &&
               region.Y + region.Height <= ReferenceHeight;
    }
}

/// <summary>
/// Parsing and scaling for <see cref="ParsecLoginLayout"/>, ported from 0jay-v2
/// <c>agent/parsec_layout.go</c> (which ports agent-parsec's <c>config.json</c> reader).
/// </summary>
/// <remarks>
/// The defaults live in the embedded <c>parsec-layout.json</c>: 1920x1080 reference,
/// e-mail 960,493 · password 960,578 · Log In 960,664 · banner 0,20,1920,140. A machine whose
/// Parsec sits elsewhere overrides any part through the service environment variable
/// <c>PARSEC_LOGIN_LAYOUT</c> with the same JSON shape, merged over the defaults. Anything
/// unreadable or outside the reference screen is refused as a whole, so a typo never sends a
/// click to a half-default, half-garbage position; the caller logs the reason and keeps the
/// defaults, exactly like agent-parsec.
/// </remarks>
public static class ParsecLayout
{
    private const int MaximumReferenceSize = 16384;

    /// <summary>agent-parsec's default positions, loaded once from the embedded resource.</summary>
    public static ParsecLoginLayout Default { get; } = LoadDefault();

    /// <summary>The environment variable a service administrator can set to move the layout.</summary>
    public const string OverrideEnvironmentVariable = "PARSEC_LOGIN_LAYOUT";

    /// <summary>
    /// Merges a <c>parsecLogin</c> JSON override over <see cref="Default"/>. Blank means the
    /// defaults. Returns <c>false</c> and an operator-readable reason for unreadable JSON,
    /// unknown fields, wrong value types or coordinates outside the reference screen.
    /// </summary>
    public static bool TryParse(string? raw, out ParsecLoginLayout layout, out string? error) =>
        TryParse(raw, Default, out layout, out error);

    /// <summary>Merges the override over <see cref="Default"/> and throws on invalid input.</summary>
    /// <exception cref="FormatException">The override is not a valid parsecLogin object.</exception>
    public static ParsecLoginLayout Parse(string? raw)
    {
        if (TryParse(raw, out var layout, out var error))
            return layout;
        throw new FormatException(error);
    }

    /// <summary>
    /// Powerless copy of the script arithmetic
    /// <c>$screen.Left + [int][Math]::Round($ref * $screen.Width / $refWidth)</c>:
    /// .NET (and PowerShell) round halves to even, which the scaling tests pin down.
    /// </summary>
    public static int ScaleCoordinate(int origin, int reference, int size, int referenceSize)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(referenceSize);
        return origin + (int)Math.Round((double)reference * size / referenceSize);
    }

    /// <summary>The real screen position of a reference point.</summary>
    public static ParsecPoint ScalePoint(ParsecPoint point, ParsecScreenBounds screen, ParsecLoginLayout layout) => new(
        ScaleCoordinate(screen.Left, point.X, screen.Width, layout.ReferenceWidth),
        ScaleCoordinate(screen.Top, point.Y, screen.Height, layout.ReferenceHeight));

    /// <summary>The real screen rectangle of the notification banner region.</summary>
    public static ParsecRegion ScaleRegion(ParsecRegion region, ParsecScreenBounds screen, ParsecLoginLayout layout) => new(
        ScaleCoordinate(screen.Left, region.X, screen.Width, layout.ReferenceWidth),
        ScaleCoordinate(screen.Top, region.Y, screen.Height, layout.ReferenceHeight),
        ScaleCoordinate(0, region.Width, screen.Width, layout.ReferenceWidth),
        ScaleCoordinate(0, region.Height, screen.Height, layout.ReferenceHeight));

    private static bool TryParse(string? raw, ParsecLoginLayout baseline, out ParsecLoginLayout layout, out string? error)
    {
        layout = baseline;
        error = null;
        if (string.IsNullOrWhiteSpace(raw))
            return true;

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(raw, new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow
            });
        }
        catch (JsonException)
        {
            error = "PARSEC_LOGIN_LAYOUT is not a parsecLogin JSON object";
            return false;
        }

        using (document)
        {
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                error = "PARSEC_LOGIN_LAYOUT is not a parsecLogin JSON object";
                return false;
            }

            var candidate = baseline;
            foreach (var property in document.RootElement.EnumerateObject())
            {
                switch (property.Name)
                {
                    case "referenceWidth":
                        if (!TryReadInt(property.Value, out var referenceWidth)) { error = "referenceWidth must be an integer"; return false; }
                        candidate = candidate with { ReferenceWidth = referenceWidth };
                        break;
                    case "referenceHeight":
                        if (!TryReadInt(property.Value, out var referenceHeight)) { error = "referenceHeight must be an integer"; return false; }
                        candidate = candidate with { ReferenceHeight = referenceHeight };
                        break;
                    case "emailPos":
                        if (!TryReadPoint(property.Value, out var emailPos)) { error = "emailPos must be {x,y}"; return false; }
                        candidate = candidate with { EmailPos = emailPos };
                        break;
                    case "passwordPos":
                        if (!TryReadPoint(property.Value, out var passwordPos)) { error = "passwordPos must be {x,y}"; return false; }
                        candidate = candidate with { PasswordPos = passwordPos };
                        break;
                    case "loginButtonPos":
                        if (!TryReadPoint(property.Value, out var loginButtonPos)) { error = "loginButtonPos must be {x,y}"; return false; }
                        candidate = candidate with { LoginButtonPos = loginButtonPos };
                        break;
                    case "notificationRegion":
                        if (!TryReadRegion(property.Value, out var notificationRegion)) { error = "notificationRegion must be {x,y,width,height}"; return false; }
                        candidate = candidate with { NotificationRegion = notificationRegion };
                        break;
                    default:
                        error = $"PARSEC_LOGIN_LAYOUT has an unknown field '{property.Name}'";
                        return false;
                }
            }

            // Coordinates from an earlier agent-parsec release that were measured wrong:
            // agent-parsec replaces them with the defaults, and so does this.
            if (candidate.EmailPos == new ParsecPoint(960, 460) &&
                candidate.PasswordPos == new ParsecPoint(960, 520) &&
                candidate.LoginButtonPos == new ParsecPoint(960, 610))
            {
                candidate = candidate with
                {
                    EmailPos = Default.EmailPos,
                    PasswordPos = Default.PasswordPos,
                    LoginButtonPos = Default.LoginButtonPos
                };
            }
            if (candidate.NotificationRegion is { X: 660, Y: 120, Width: 600, Height: 100 } or
                                                { X: 650, Y: 100, Width: 620, Height: 800 })
            {
                candidate = candidate with { NotificationRegion = Default.NotificationRegion };
            }

            if (!candidate.IsValid())
            {
                error = "PARSEC_LOGIN_LAYOUT is outside the reference screen";
                return false;
            }

            layout = candidate;
            return true;
        }
    }

    private static bool TryReadInt(JsonElement element, out int value)
    {
        value = 0;
        return element.ValueKind == JsonValueKind.Number && element.TryGetInt32(out value);
    }

    private static bool TryReadPoint(JsonElement element, out ParsecPoint point)
    {
        point = default;
        if (element.ValueKind != JsonValueKind.Object)
            return false;
        var x = 0;
        var y = 0;
        foreach (var property in element.EnumerateObject())
        {
            switch (property.Name)
            {
                case "x":
                    if (!TryReadInt(property.Value, out x)) return false;
                    break;
                case "y":
                    if (!TryReadInt(property.Value, out y)) return false;
                    break;
                default:
                    return false;
            }
        }
        point = new ParsecPoint(x, y);
        return true;
    }

    private static bool TryReadRegion(JsonElement element, out ParsecRegion region)
    {
        region = default;
        if (element.ValueKind != JsonValueKind.Object)
            return false;
        var x = 0;
        var y = 0;
        var width = 0;
        var height = 0;
        foreach (var property in element.EnumerateObject())
        {
            switch (property.Name)
            {
                case "x":
                    if (!TryReadInt(property.Value, out x)) return false;
                    break;
                case "y":
                    if (!TryReadInt(property.Value, out y)) return false;
                    break;
                case "width":
                    if (!TryReadInt(property.Value, out width)) return false;
                    break;
                case "height":
                    if (!TryReadInt(property.Value, out height)) return false;
                    break;
                default:
                    return false;
            }
        }
        region = new ParsecRegion(x, y, width, height);
        return true;
    }

    private static ParsecLoginLayout LoadDefault()
    {
        // Bootstrap copy of agent-parsec's config.json values so parsing the resource cannot
        // recurse through Default; the embedded JSON is the actual source of truth.
        var bootstrap = new ParsecLoginLayout(
            1920,
            1080,
            new ParsecPoint(960, 493),
            new ParsecPoint(960, 578),
            new ParsecPoint(960, 664),
            new ParsecRegion(0, 20, 1920, 140));
        if (!TryParse(ParsecResources.ReadText(ParsecResources.LayoutJson), bootstrap, out var layout, out var error))
            throw new InvalidOperationException($"The embedded default Parsec login layout is invalid: {error}");
        return layout;
    }
}

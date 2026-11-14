namespace TopupLedger.Infrastructure.Configuration;

public sealed class SecretsOptions
{
    public const string SectionName = "Secrets";

    /// <summary>AES-GCM master key for partner secret encryption. 32 bytes (256-bit).</summary>
    public string MasterKey { get; set; } = string.Empty;
}

public sealed class AdminApiOptions
{
    public const string SectionName = "AdminApi";

    /// <summary>Static admin API key used to authenticate report requests (X-Admin-Key header).</summary>
    public string AdminApiKey { get; set; } = string.Empty;
}

public sealed class OperatorCallbackOptions
{
    public const string SectionName = "OperatorCallbacks";

    /// <summary>Shared secrets for operator callbacks, keyed by operator code (etisalat, du).</summary>
    public Dictionary<string, string> Tokens { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

public sealed class OperatorSimulationOptions
{
    public const string SectionName = "OperatorSimulation";
    public bool Enabled { get; set; } = true;
    /// <summary>Probability (0.0-1.0) of SUCCESS, FAILED, and no-callback.</summary>
    public double SuccessProbability { get; set; } = 0.85;
    public double FailedProbability { get; set; } = 0.10;
    /// <summary>Min/max callback delay in seconds.</summary>
    public int MinDelaySeconds { get; set; } = 1;
    public int MaxDelaySeconds { get; set; } = 15;
    /// <summary>Seconds without callback after which a top-up is marked EXPIRED.</summary>
    public int ExpireAfterSeconds { get; set; } = 120;
    /// <summary>Poll interval for the expire worker.</summary>
    public int ExpirePollSeconds { get; set; } = 5;
}

public sealed class ConnectionStringsOptions
{
    public const string SectionName = "ConnectionStrings";
    public string Default { get; set; } = string.Empty;
}

public sealed class CorsOptions
{
    public const string SectionName = "Cors";
    public string[] AllowedOrigins { get; set; } = Array.Empty<string>();
}

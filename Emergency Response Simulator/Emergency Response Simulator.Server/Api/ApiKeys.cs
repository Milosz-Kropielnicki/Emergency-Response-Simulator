using System.Security.Cryptography;
using System.Text;
using Emergency_Response_Simulator.Core.Model;
using Microsoft.Extensions.Options;

namespace Emergency_Response_Simulator.Server.Api;

/// <summary>An outside system allowed to use the REST API (Phase 20), from the "Server:ApiKeys" configuration.</summary>
public sealed class ApiKeyOptions
{
    /// <summary>Who it is, e.g. "County CAD bridge": how its actions appear in the record.</summary>
    public string Name { get; set; } = "";

    /// <summary>The secret it sends as X-Api-Key (or a bearer token). At least 16 characters; shorter keys are ignored.</summary>
    public string Key { get; set; } = "";

    /// <summary>
    /// The role it acts in. Observer (the default) reads the COP and exports; a player role may also give that role's
    /// commands, under the permission matrix; Instructor also sees ground truth.
    /// </summary>
    public UserRole Role { get; set; } = UserRole.Observer;

    /// <summary>May bring data in: import standards documents and feed vehicle positions.</summary>
    public bool Feed { get; set; }
}

/// <summary>An authenticated API caller.</summary>
public sealed record ApiClient(string Name, UserRole Role, bool Feed)
{
    public bool Truth => Roles.SeesTruth(Role);
}

/// <summary>Checks the key a request carries against the configured ones, in constant time.</summary>
public sealed class ApiKeys(IOptions<ServerOptions> options, ILogger<ApiKeys> logger)
{
    public const string Header = "X-Api-Key";
    public const int MinimumLength = 16;

    private readonly IReadOnlyList<(byte[] Key, ApiClient Client)> _keys = Load(options.Value.ApiKeys, logger);

    public bool Any => _keys.Count > 0;

    private static List<(byte[], ApiClient)> Load(IEnumerable<ApiKeyOptions> configured, ILogger logger)
    {
        var keys = new List<(byte[], ApiClient)>();
        foreach (var key in configured)
        {
            if (string.IsNullOrWhiteSpace(key.Name) || key.Key.Length < MinimumLength)
            {
                logger.LogWarning("API key {Name} ignored: it needs a name and a key of at least {Length} characters.", key.Name, MinimumLength);
                continue;
            }
            keys.Add((Encoding.UTF8.GetBytes(key.Key), new ApiClient(key.Name.Trim(), key.Role, key.Feed)));
        }
        return keys;
    }

    /// <summary>The caller, from X-Api-Key or "Authorization: Bearer …"; null when neither matches a key.</summary>
    public ApiClient? Authenticate(HttpRequest request)
    {
        var presented = request.Headers[Header].FirstOrDefault()
                        ?? (request.Headers.Authorization.FirstOrDefault() is { } auth && auth.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)
                            ? auth["Bearer ".Length..].Trim()
                            : null);
        if (string.IsNullOrEmpty(presented)) return null;
        var bytes = Encoding.UTF8.GetBytes(presented);
        ApiClient? match = null;
        foreach (var (key, client) in _keys)
            if (CryptographicOperations.FixedTimeEquals(key, bytes)) match ??= client;
        return match;
    }
}

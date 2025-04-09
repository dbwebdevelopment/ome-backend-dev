using System.IdentityModel.Tokens.Jwt;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.Tokens;
using ome.Core.Interfaces.Services;

namespace ome.Infrastructure.Identity.Keycloak;

/// <summary>
/// Implementierung des KeycloakService für die Interaktion mit dem Keycloak Identity Server
/// </summary>
public class KeycloakService : IKeycloakService {
    private readonly HttpClient _httpClient;
    private readonly KeycloakSettings _settings;
    private readonly ILogger<KeycloakService> _logger;
    private readonly JwtSecurityTokenHandler _tokenHandler;

    public KeycloakService(
        HttpClient httpClient,
        IConfiguration configuration,
        ILogger<KeycloakService> logger) {
        _httpClient = httpClient;

        _settings = configuration.GetSection("Keycloak").Get<KeycloakSettings>()
                    ?? throw new ArgumentNullException(nameof(configuration), "Keycloak configuration not found");
        _logger = logger;
        _tokenHandler = new JwtSecurityTokenHandler();

        // Konfiguriere den HTTP-Client
        _httpClient.BaseAddress = new Uri(_settings.BaseUrl);
        _httpClient.DefaultRequestHeaders.Accept.Clear();
        _httpClient.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
    }

    /// <summary>
    /// Generiert die Autorisierungs-URL für den OAuth-Flow
    /// </summary>
    public string GetAuthorizationUrl(string redirectUri, string state) {
        var authUrl = $"{_settings.BaseUrl}/auth/realms/{_settings.Realm}/protocol/openid-connect/auth";
        
        var queryParams = new Dictionary<string, string>
        {
            ["response_type"] = "code",
            ["client_id"] = _settings.ClientId,
            ["redirect_uri"] = redirectUri,
            ["state"] = state,
            ["scope"] = "openid profile email"
        };

        var queryString = string.Join("&", queryParams.Select(kv => $"{kv.Key}={Uri.EscapeDataString(kv.Value)}"));
        return $"{authUrl}?{queryString}";
    }

    /// <summary>
    /// Tauscht den Autorisierungscode gegen Tokens aus
    /// </summary>
    public async Task<(string AccessToken, string RefreshToken)> ExchangeCodeForTokenAsync(
        string code,
        string redirectUri,
        CancellationToken cancellationToken = default) {
        try
        {
            _logger.LogDebug("Exchanging code for token");

            var tokenEndpoint = $"{_settings.BaseUrl}/realms/{_settings.Realm}/protocol/openid-connect/token";

            var content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["grant_type"] = "authorization_code",
                ["client_id"] = _settings.ClientId,
                ["client_secret"] = _settings.ClientSecret,
                ["code"] = code,
                ["redirect_uri"] = redirectUri
            });

            var response = await _httpClient.PostAsync(tokenEndpoint, content, cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                var errorContent = await response.Content.ReadAsStringAsync(cancellationToken);

                _logger.LogWarning("Code exchange failed. Status code: {StatusCode}, Error: {Error}",
                    response.StatusCode, errorContent);
                throw new InvalidOperationException($"Code exchange failed: {response.StatusCode}");
            }

            var responseJson = await response.Content.ReadAsStringAsync(cancellationToken);
            var tokenResponse = JsonSerializer.Deserialize<KeycloakTokenResponse>(responseJson);

            _logger.LogInformation("Code exchange successful");

            return (tokenResponse!.AccessToken, tokenResponse.RefreshToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error during code exchange");
            throw;
        }
    }

    /// <summary>
    /// Erneuert ein Token mithilfe eines Refresh-Tokens
    /// </summary>
    public async Task<string> RefreshTokenAsync(
        string refreshToken,
        CancellationToken cancellationToken = default) {
        try
        {
            _logger.LogDebug("Refreshing token");

            var tokenEndpoint = $"{_settings.BaseUrl}/auth/realms/{_settings.Realm}/protocol/openid-connect/token";

            var content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["grant_type"] = "refresh_token",
                ["client_id"] = _settings.ClientId,
                ["client_secret"] = _settings.ClientSecret,
                ["refresh_token"] = refreshToken
            });

            var response = await _httpClient.PostAsync(tokenEndpoint, content, cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                var errorContent = await response.Content.ReadAsStringAsync(cancellationToken);

                _logger.LogWarning("Token refresh failed. Status code: {StatusCode}, Error: {Error}",
                    response.StatusCode, errorContent);
                throw new InvalidOperationException($"Token refresh failed: {response.StatusCode}");
            }

            var responseJson = await response.Content.ReadAsStringAsync(cancellationToken);
            var tokenResponse = JsonSerializer.Deserialize<KeycloakTokenResponse>(responseJson);

            _logger.LogInformation("Token refresh successful");

            return tokenResponse!.AccessToken;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error during token refresh");
            throw;
        }
    }

    /// <summary>
    /// Führt den Logout-Vorgang durch
    /// </summary>
    public async Task LogoutAsync(
        string refreshToken,
        CancellationToken cancellationToken = default) {
        try
        {
            _logger.LogDebug("Logging out user");

            var logoutEndpoint = $"{_settings.BaseUrl}/auth/realms/{_settings.Realm}/protocol/openid-connect/logout";

            var content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["refresh_token"] = refreshToken,
                ["client_id"] = _settings.ClientId,
                ["client_secret"] = _settings.ClientSecret
            });

            var response = await _httpClient.PostAsync(logoutEndpoint, content, cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                var errorContent = await response.Content.ReadAsStringAsync(cancellationToken);

                _logger.LogWarning("Logout failed. Status code: {StatusCode}, Error: {Error}",
                    response.StatusCode, errorContent);
                // Wir werfen hier keine Exception, da der Logout-Vorgang nicht kritisch ist
            }

            _logger.LogInformation("Logout successful");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error during logout");
            // Wir werfen hier keine Exception, da der Logout-Vorgang nicht kritisch ist
        }
    }

    /// <summary>
    /// Validiert ein Token
    /// </summary>
    public async Task<bool> ValidateTokenAsync(
        string token,
        CancellationToken cancellationToken = default) {
        try
        {
            _logger.LogDebug("Validating token");

            // Cache für JWKS-Endpoint implementieren, um häufige Anfragen zu vermeiden
            
            // Hole die JWKS-URL, um die Schlüssel zum Validieren zu erhalten
            var jwksEndpoint = $"{_settings.BaseUrl}/auth/realms/{_settings.Realm}/protocol/openid-connect/certs";
            var jwksResponse = await _httpClient.GetAsync(jwksEndpoint, cancellationToken);

            if (!jwksResponse.IsSuccessStatusCode)
            {
                _logger.LogWarning("Failed to retrieve JWKS. Status code: {StatusCode}", jwksResponse.StatusCode);
                return false;
            }

            var jwksJson = await jwksResponse.Content.ReadAsStringAsync(cancellationToken);
            var jwks = JsonSerializer.Deserialize<JsonWebKeySet>(jwksJson);

            // Konfiguriere die Token-Validierungsparameter
            var validationParameters = new TokenValidationParameters
            {
                ValidateIssuer = _settings.ValidateIssuer,
                ValidIssuer = $"{_settings.BaseUrl}/auth/realms/{_settings.Realm}",
                ValidateAudience = _settings.ValidateAudience,
                ValidAudience = _settings.ClientId,
                ValidateLifetime = true,
                IssuerSigningKeys = jwks!.Keys
            };

            // Validiere das Token
            var principal = _tokenHandler.ValidateToken(token, validationParameters, out var validatedToken);

            return validatedToken != null && validatedToken.ValidTo > DateTime.UtcNow;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error validating token");
            return false;
        }
    }

    /// <summary>
    /// Extrahiert die TenantId aus einem Token
    /// </summary>
    public Task<Guid> GetTenantIdFromTokenAsync(
        string token,
        CancellationToken cancellationToken = default) {
        try
        {
            _logger.LogDebug("Extracting tenant ID from token");

            var jwtToken = _tokenHandler.ReadJwtToken(token);

            // Suche nach dem Tenant-Claim
            var tenantClaim = jwtToken.Claims.FirstOrDefault(c => c.Type == "tenant_id" || c.Type == "tenantId");

            if (tenantClaim == null)
            {
                _logger.LogWarning("No tenant ID claim found in token");
                return Task.FromResult(Guid.Empty);
            }

            if (Guid.TryParse(tenantClaim.Value, out var tenantId))
            {
                _logger.LogInformation("Extracted tenant ID {TenantId} from token", tenantId);
                return Task.FromResult(tenantId);
            }

            _logger.LogWarning("Invalid tenant ID format in token: {TenantId}", tenantClaim.Value);
            return Task.FromResult(Guid.Empty);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error extracting tenant ID from token");
            return Task.FromResult(Guid.Empty);
        }
    }

    /// <summary>
    /// Extrahiert die Informationen aus einem Token
    /// </summary>
    public Task<TokenInfo> GetTokenInfoAsync(string token, CancellationToken cancellationToken = default)
    {
        try
        {
            _logger.LogDebug("Extracting token information");

            var jwtToken = _tokenHandler.ReadJwtToken(token);
            
            var tokenInfo = new TokenInfo
            {
                UserId = jwtToken.Claims.FirstOrDefault(c => c.Type == "sub")?.Value,
                Username = jwtToken.Claims.FirstOrDefault(c => c.Type == "preferred_username")?.Value,
                Email = jwtToken.Claims.FirstOrDefault(c => c.Type == "email")?.Value,
                TenantId = jwtToken.Claims.FirstOrDefault(c => c.Type == "tenant_id" || c.Type == "tenantId")?.Value,
                ExpiresAt = jwtToken.ValidTo,
                Roles = jwtToken.Claims.Where(c => c.Type == "roles" || c.Type == "role").Select(c => c.Value).ToList()
            };

            return Task.FromResult(tokenInfo);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error extracting token information");
            throw;
        }
    }
}

/// <summary>
/// Konfigurationseinstellungen für Keycloak
/// </summary>
public class KeycloakSettings {
    public string BaseUrl { get; init; } = null!;
    public string Realm { get; init; } = null!;
    public string ClientId { get; init; } = null!;
    public string ClientSecret { get; init; } = null!;
    public bool ValidateIssuer { get; init; } = true;
    public bool ValidateAudience { get; init; } = true;
    public bool RequireHttpsMetadata { get; init; } = true;
}

/// <summary>
/// Antwortmodell für Token-Antworten von Keycloak
/// </summary>
public class KeycloakTokenResponse {
    [JsonPropertyName("access_token")] public string AccessToken { get; init; } = null!;

    [JsonPropertyName("expires_in")] public int ExpiresIn { get; init; }

    [JsonPropertyName("refresh_token")] public string RefreshToken { get; init; } = null!;

    [JsonPropertyName("refresh_expires_in")]
    public int RefreshExpiresIn { get; init; }

    [JsonPropertyName("token_type")] public string TokenType { get; init; } = null!;

    [JsonPropertyName("scope")] public string Scope { get; init; } = null!;
}

/// <summary>
/// Modell für JSON Web Key Set
/// </summary>
public class JsonWebKeySet {
    [JsonPropertyName("keys")] public List<JsonWebKey> Keys { get; init; } = null!;
}

/// <summary>
/// Modell für JSON Web Key
/// </summary>
public class JsonWebKey: SecurityKey {
    [JsonPropertyName("kty")] public string KeyType { get; set; } = null!;

    [JsonPropertyName("kid")] public new string KeyId { get; set; } = null!;

    [JsonPropertyName("use")] public string Use { get; set; } = null!;

    [JsonPropertyName("n")] public string Modulus { get; set; } = null!;

    [JsonPropertyName("e")] public string Exponent { get; set; } = null!;

    [JsonPropertyName("alg")] public string Algorithm { get; set; } = null!;

    [JsonPropertyName("x5c")] public string[] X509Certificates { get; set; } = null!;

    public override int KeySize => 2048; // Default für RSA
}

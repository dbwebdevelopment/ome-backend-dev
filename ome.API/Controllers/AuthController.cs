using System.IdentityModel.Tokens.Jwt;
using Microsoft.AspNetCore.Mvc;
using System.Web;
using ome.Core.Interfaces.Services;

namespace ome.API.Controllers;

[ApiController]
[Route("api/[controller]")]
public class AuthController(
    IKeycloakService keycloakService,
    ITenantService tenantService,
    ILogger<AuthController> logger,
    IConfiguration configuration)
    : ControllerBase {
    private readonly ITenantService _tenantService = tenantService;

    [HttpGet("login")]
    public IActionResult Login([FromQuery] string redirectUri = "/dashboard") {
        try
        {
            logger.LogInformation("Generating Keycloak login URL with redirect: {RedirectUri}", redirectUri);

            // Generate a state parameter for CSRF protection
            var state = Guid.NewGuid().ToString();

            // Get Keycloak configuration
            var baseUrl = configuration["Keycloak:BaseUrl"]
                          ?? throw new InvalidOperationException("Keycloak Base URL not configured");

            var realm = configuration["Keycloak:Realm"]
                        ?? throw new InvalidOperationException("Keycloak Realm not configured");

            var clientId = configuration["Keycloak:ClientId"]
                           ?? throw new InvalidOperationException("Keycloak Client ID not configured");

            // Construct the callback URL
            var scheme = Request.Scheme;
            var host = Request.Host.ToString();
            var callbackUrl = $"{scheme}://{host}/api/Auth/callback";

            // URL encode the parameters
            var encodedCallbackUrl = HttpUtility.UrlEncode(callbackUrl);
            var encodedState = HttpUtility.UrlEncode(state);
            var encodedClientId = HttpUtility.UrlEncode(clientId);

            // Construct the full Keycloak authorization URL
            var authUrl = $"{baseUrl}/realms/{realm}/protocol/openid-connect/auth" +
                          $"?response_type=code" +
                          $"&client_id={encodedClientId}" +
                          $"&redirect_uri={encodedCallbackUrl}" +
                          $"&state={encodedState}" +
                          $"&scope=openid%20profile%20email";

            logger.LogInformation("Generated Keycloak Authorization URL: {AuthUrl}", authUrl);

            // Store redirect URI and state in session
            HttpContext.Session.SetString("redirect_uri", redirectUri);
            HttpContext.Session.SetString("oauth_state", state);

            // Important: Ensure session is committed before redirect
            HttpContext.Session.CommitAsync().Wait();

            // Redirect to Keycloak login page
            return Redirect(authUrl);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to generate login URL: {ErrorMessage}", ex.Message);

            return StatusCode(500, new ErrorResponse
            {
                Message = $"Failed to generate login URL: {ex.Message}"
            });
        }
    }

    [HttpGet("callback")]
    public async Task<IActionResult> Callback([FromQuery] string code, [FromQuery] string state) {
        try
        {
            logger.LogInformation("OAuth callback received with state: {State}", state);

            // Retrieve stored state
            var storedState = HttpContext.Session.GetString("oauth_state");
            logger.LogInformation("Stored state from session: {StoredState}", storedState ?? "null");

            // Only validate state if it exists in session
            if (!string.IsNullOrEmpty(storedState) && storedState != state)
            {
                logger.LogWarning("State mismatch: expected {StoredState}, received {State}", storedState, state);
                return BadRequest("Invalid state parameter");
            }

            // Retrieve stored redirect URI
            var redirectUri = HttpContext.Session.GetString("redirect_uri");
            logger.LogInformation("Retrieved redirect URI from session: {RedirectUri}", redirectUri ?? "null");

            // Construct callback URL
            var scheme = Request.Scheme;
            var host = Request.Host.ToString();
            var callbackUrl = $"{scheme}://{host}/api/Auth/callback";

            // Exchange code for tokens
            logger.LogInformation("Exchanging code for tokens...");
            var (accessToken, refreshToken) = await keycloakService.ExchangeCodeForTokenAsync(code, callbackUrl);
            logger.LogInformation("Token exchange successful");

            if (string.IsNullOrEmpty(accessToken))
            {
                logger.LogError("Received empty access token from token exchange");
                return BadRequest("Failed to retrieve access token");
            }

            // Decode the JWT token and extract claims
            var jwtHandler = new JwtSecurityTokenHandler();
            var jwtToken = jwtHandler.ReadJwtToken(accessToken);

            // Extract groups (companyId) from token claims
            var groupsClaim = jwtToken?.Claims
                .FirstOrDefault(c => c.Type == "groups")?.Value;

            // Log all claims for debugging
            logger.LogInformation("JWT Claims: {Claims}", 
                string.Join(", ", jwtToken?.Claims.Select(c => $"{c.Type}={c.Value}") ?? Array.Empty<string>()));

            // Handle the case where there is no group found for the user
            if (string.IsNullOrEmpty(groupsClaim))
            {
                logger.LogWarning("No group (companyId) found for user in the 'groups' claim");
                return Redirect("/auth/error?message=No+group+found");
            }

            // Assuming the companyId is part of the group name, split and extract it
            var companyId = groupsClaim.Split("/").LastOrDefault();

            if (string.IsNullOrEmpty(companyId))
            {
                logger.LogWarning("No company group found for user");
                return Redirect("/auth/error?message=No+company+group+assigned");
            }

            // Get the Frontend URL from configuration
            var frontendBaseUrl = configuration["Frontend:BaseUrl"];

            if (string.IsNullOrEmpty(frontendBaseUrl))
            {
                // Fallback: use the current host URL
                frontendBaseUrl = $"{Request.Scheme}://{Request.Host}";
                logger.LogWarning("Frontend:BaseUrl not configured, using: {FrontendBaseUrl}", frontendBaseUrl);
            }

            // Prepare the redirect path (without the base URL)
            string redirectPath;

            if (redirectUri == "/dashboard" || string.IsNullOrEmpty(redirectUri))
            {
                redirectPath = $"/dashboard/{companyId}";
            }
            else if (redirectUri.StartsWith("/dashboard") && !redirectUri.Contains(companyId))
            {
                redirectPath = $"/dashboard/{companyId}";
            }
            else
            {
                redirectPath = redirectUri;
            }

            // Create full redirect URL with frontend base
            string fullRedirectUrl = $"{frontendBaseUrl.TrimEnd('/')}{redirectPath}";
            logger.LogInformation("Authentication successful, redirecting to: {RedirectUri}", fullRedirectUrl);

            // Set cookies with longer expiration and proper path
            var cookieOptions = new CookieOptions
            {
                HttpOnly = true,
                Secure = true, 
                SameSite = SameSiteMode.None,
                Expires = DateTime.UtcNow.AddMinutes(5), // Extend to 1 hour
                Path = "/" // Ensure cookies are available site-wide
            };

            Response.Cookies.Append("access_token", accessToken, cookieOptions);
            Response.Cookies.Append("refresh_token", refreshToken, cookieOptions);
            Response.Cookies.Append("tenant_id", companyId, cookieOptions);

            // Clear session state
            HttpContext.Session.Remove("oauth_state");
            HttpContext.Session.Remove("redirect_uri");

            // Ensure session changes are committed
            await HttpContext.Session.CommitAsync();

            // Redirect to frontend URL
            return Redirect(fullRedirectUrl);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "OAuth callback processing failed: {ErrorMessage}", ex.Message);

            // Get the Frontend URL for the error page
            var frontendBaseUrl = configuration["Frontend:BaseUrl"] ?? $"{Request.Scheme}://{Request.Host}";
            return Redirect($"{frontendBaseUrl.TrimEnd('/')}/auth/error?message=Authentication+failed");
        }
    }

    [HttpGet("logout")]
    public async Task<IActionResult> Logout([FromQuery] string? redirectUri) {
        try
        {
            logger.LogInformation("Logout request received");

            // Retrieve refresh token from cookies
            var refreshToken = Request.Cookies["refresh_token"];

            if (!string.IsNullOrEmpty(refreshToken))
            {
                // Perform logout via Keycloak service
                await keycloakService.LogoutAsync(refreshToken);

                // Remove authentication cookies - use consistent path with cookie creation
                string[] cookiesToRemove = ["access_token", "refresh_token", "tenant_id"];

                foreach (var cookieName in cookiesToRemove)
                {
                    Response.Cookies.Delete(cookieName, new CookieOptions { Path = "/" });
                }
            }

            // Clear session if exists
            HttpContext.Session.Clear();
            await HttpContext.Session.CommitAsync();

            logger.LogInformation("Logout successful");

            // Redirect to specified or default URL
            return Redirect(redirectUri ?? "/");
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Logout failed: {ErrorMessage}", ex.Message);

            // Ensure cookies are deleted even if logout fails
            string[] cookiesToRemove = ["access_token", "refresh_token", "tenant_id"];

            foreach (var cookieName in cookiesToRemove)
            {
                Response.Cookies.Delete(cookieName, new CookieOptions { Path = "/" });
            }

            return Redirect("/auth/error?message=Logout+failed");
        }
    }
}

public class ErrorResponse {
    public string Message { get; set; } = null!;
}
using System.Net.Http.Headers;
using System.Security.Claims;
using System.Text;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;
using System.Collections.Concurrent;

public sealed class BasicAuthHandler : AuthenticationHandler<AuthenticationSchemeOptions>
{
    public BasicAuthHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> opts,
        ILoggerFactory logger,
        UrlEncoder encoder,
        ISystemClock clock)
        : base(opts, logger, encoder, clock) { }

    protected override async Task HandleChallengeAsync(AuthenticationProperties props)
    {
        Response.Headers["WWW-Authenticate"] = "Basic realm=\"UploadAdmin\"";
        await base.HandleChallengeAsync(props);
    }


// in-memory session manager for admin
    public static class AdminSessionManager
    {
        private static readonly ConcurrentDictionary<string, DateTime> _sessions = new();

        public static bool HasExpired(string user)
        {
            if (_sessions.TryGetValue(user, out var lastActivity))
            {
                // If 4 minutes have passed since last activity, session has expired
                return DateTime.UtcNow - lastActivity >= TimeSpan.FromMinutes(4);
            }
            // No previous session (fresh login)
            return false;
        }

        public static void RecordActivity(string user)
        {
            _sessions[user] = DateTime.UtcNow;
        }

        public static void InvalidateSession(string user)
        {
            _sessions.TryRemove(user, out _);
        }
    }

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        // look for header
        if (!Request.Headers.ContainsKey("Authorization"))
            return Task.FromResult(AuthenticateResult.NoResult());

        // prevent null ref
        var authHeader = Request.Headers["Authorization"].FirstOrDefault();
        if (string.IsNullOrEmpty(authHeader))
            return Task.FromResult(AuthenticateResult.NoResult());
        var header = AuthenticationHeaderValue.Parse(authHeader);

        if (!"Basic".Equals(header.Scheme, StringComparison.OrdinalIgnoreCase))
            return Task.FromResult(AuthenticateResult.Fail("Only Basic authentication."));

        string user, pass;
        try
        {
            var value = Encoding.UTF8
                        .GetString(Convert.FromBase64String(header.Parameter ?? ""));
            var parts = value.Split(':', 2);
            user = parts[0]; pass = parts.Length > 1 ? parts[1] : "";
        }
        catch
        {
            return Task.FromResult(AuthenticateResult.Fail("Invalid Basic header"));
        }

        // load from configuration (secrets.json, environment variables, appsettings.json)
        var config = Context.RequestServices.GetRequiredService<IConfiguration>();
        var adminUser = config["AdminUser"];
        var adminPass = config["AdminPass"];

        if (string.IsNullOrEmpty(adminUser) || string.IsNullOrEmpty(adminPass))
        {
            global::Logger.Log("[SECURITY] Admin credentials are not configured in secrets.json or configuration!");
            return Task.FromResult(AuthenticateResult.Fail("Admin credentials not configured"));
        }

        if (user != adminUser || pass != adminPass)
            return Task.FromResult(AuthenticateResult.Fail("Bad credentials"));

        // Check if existing session has timed out (> 4 minutes idle)
        if (AdminSessionManager.HasExpired(user))
        {
            AdminSessionManager.InvalidateSession(user);
            return Task.FromResult(AuthenticateResult.Fail("Session expired. Please sign in again."));
        }

        // Valid credentials and active/new session -> record activity timestamp and succeed
        AdminSessionManager.RecordActivity(user);

        var claims = new[] { new Claim(ClaimTypes.Name, user) };
        var id = new ClaimsIdentity(claims, Scheme.Name);
        var ticket = new AuthenticationTicket(new ClaimsPrincipal(id), Scheme.Name);

        return Task.FromResult(AuthenticateResult.Success(ticket));
    }
}

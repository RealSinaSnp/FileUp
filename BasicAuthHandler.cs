using System.Net.Http.Headers;
using System.Security.Claims;
using System.Text;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

public sealed class BasicAuthHandler
    : AuthenticationHandler<AuthenticationSchemeOptions>
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

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        /* look for header */
        if (!Request.Headers.ContainsKey("Authorization"))
            return Task.FromResult(AuthenticateResult.NoResult());

        var header = AuthenticationHeaderValue.Parse(
                         Request.Headers["Authorization"]);
        if (!"Basic".Equals(header.Scheme, StringComparison.OrdinalIgnoreCase))
            return Task.FromResult(
                AuthenticateResult.Fail("Only Basic authentication."));

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
            return Task.FromResult(
                AuthenticateResult.Fail("Invalid Basic header"));
        }

        /* single hard-coded account for demo */
        const string ADMIN_USER = "admin";
        const string ADMIN_PASS = "kali";

        if (user != ADMIN_USER || pass != ADMIN_PASS)
            return Task.FromResult(
                AuthenticateResult.Fail("Bad credentials"));

        var claims  = new[] { new Claim(ClaimTypes.Name, user) };
        var id      = new ClaimsIdentity(claims, Scheme.Name);
        var ticket  = new AuthenticationTicket(
                          new ClaimsPrincipal(id), Scheme.Name);

        return Task.FromResult(AuthenticateResult.Success(ticket));
    }
}

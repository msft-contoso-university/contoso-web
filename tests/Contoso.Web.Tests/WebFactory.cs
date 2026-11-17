using System.Net;
using System.Security.Claims;
using System.Text;
using System.Text.Encodings.Web;
using Contoso.Web.Services;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Contoso.Web.Tests;

public sealed class TestTokens : IDelegatedTokens
{
    public List<string[]> Requests { get; } = [];
    public Task<string> GetAsync(string[] scopes)
    {
        Requests.Add(scopes);
        return Task.FromResult("controlled-test-token");
    }
}

public sealed record RecordedRequest(string Method, Uri Uri, string? Authorization, string? Body);

public sealed class ControlledHandler : HttpMessageHandler
{
    public List<RecordedRequest> Requests { get; } = [];
    public Func<RecordedRequest, HttpResponseMessage> Respond { get; set; } = _ =>
        Json("""{"id":7,"firstName":"Ada","lastName":"Lovelace","enrollmentDate":"2024-09-01T00:00:00Z"}""");

    public static HttpResponseMessage Json(string json, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var recorded = new RecordedRequest(request.Method.Method, request.RequestUri!,
            request.Headers.Authorization?.ToString(),
            request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken));
        Requests.Add(recorded);
        return Respond(recorded);
    }
}

public sealed class TestAuthentication(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!Request.Headers.ContainsKey("X-Test-User"))
        {
            return Task.FromResult(AuthenticateResult.NoResult());
        }
        var identity = new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, "test-person"), new Claim("name", "Test person")], Scheme.Name);
        return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), Scheme.Name)));
    }
}

public sealed class WebFactory : WebApplicationFactory<Program>
{
    public ControlledHandler Handler { get; } = new();
    public TestTokens Tokens { get; } = new();

    public static Dictionary<string, string?> Configuration()
    {
        var result = new Dictionary<string, string?>
        {
            ["AzureAd:TenantId"] = "11111111-1111-1111-1111-111111111111",
            ["AzureAd:ClientId"] = "22222222-2222-2222-2222-222222222222",
            ["AzureAd:ClientCredentials:0:SourceType"] = "SignedAssertionFromManagedIdentity"
        };
        foreach (var name in ServiceConfiguration.Names)
        {
            var resource = $"api://test-{name.ToLowerInvariant()}/{name.ToLowerInvariant()}";
            result[$"Services:{name}:BaseUrl"] = $"https://{name.ToLowerInvariant()}.example.test/";
            result[$"Services:{name}:ReadScopes:0"] = resource + ".read";
            if (name == "Enrollments")
            {
                result[$"Services:{name}:WriteScopes:0"] = resource + ".read";
                result[$"Services:{name}:WriteScopes:1"] = resource + ".write";
            }
        }
        return result;
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(Configuration()));
        builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<IDelegatedTokens>();
            services.AddSingleton<IDelegatedTokens>(Tokens);
            services.AddAuthentication(options =>
            {
                options.DefaultAuthenticateScheme = "Test";
                options.DefaultChallengeScheme = "Test";
            }).AddScheme<AuthenticationSchemeOptions, TestAuthentication>("Test", _ => { });
            foreach (var name in ServiceConfiguration.Names)
            {
                services.AddHttpClient(name).ConfigurePrimaryHttpMessageHandler(() => Handler);
            }
        });
    }

    public HttpClient Browser(bool signedIn = true)
    {
        var client = CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri("https://localhost"),
            AllowAutoRedirect = false
        });
        if (signedIn)
        {
            client.DefaultRequestHeaders.Add("X-Test-User", "yes");
        }
        return client;
    }
}

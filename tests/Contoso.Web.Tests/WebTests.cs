using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.Identity.Abstractions;
using Microsoft.Identity.Web;
using Microsoft.VisualStudio.TestTools.UnitTesting;

[assembly: Parallelize(Scope = ExecutionScope.MethodLevel)]

namespace Contoso.Web.Tests;

[TestClass]
public sealed class WebTests
{
    [TestMethod]
    public async Task AppServiceHttpsBoundaryIgnoresSpoofedHeadersWithoutRedirectLoops()
    {
        await using var original = new WebFactory();
        await using var app = original.WithWebHostBuilder(builder =>
        {
            builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    ["Hosting:AppServiceHttpsOnly"] = "true",
                    ["WEBSITE_SITE_NAME"] = "web",
                    ["WEBSITE_HOSTNAME"] = "web.example.test",
                    ["AllowedHosts"] = "web.example.test"
                }));
            builder.ConfigureTestServices(services =>
                services.Configure<OpenIdConnectOptions>(OpenIdConnectDefaults.AuthenticationScheme, options =>
                    options.ConfigurationManager = new StaticConfigurationManager<OpenIdConnectConfiguration>(
                        new OpenIdConnectConfiguration
                        {
                            Issuer = "https://login.example.test",
                            AuthorizationEndpoint = "https://login.example.test/authorize"
                        })));
        });
        using var browser = app.CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri("http://web.example.test"),
            AllowAutoRedirect = false
        });
        browser.DefaultRequestHeaders.Add("X-Forwarded-Proto", "http");
        browser.DefaultRequestHeaders.Add("X-Forwarded-Host", "attacker.example.test");
        Assert.AreEqual(HttpStatusCode.OK, (await browser.GetAsync("/health/live")).StatusCode);
        var signIn = await browser.GetAsync("/account/signin");
        Assert.AreEqual(HttpStatusCode.Redirect, signIn.StatusCode);
        var location = signIn.Headers.Location!;
        Assert.AreEqual("login.example.test", location.Host);
        Assert.AreEqual("https://web.example.test/signin-oidc",
            QueryHelpers.ParseQuery(location.Query)["redirect_uri"].ToString());
        browser.DefaultRequestHeaders.Host = "attacker.example.test";
        Assert.AreEqual(HttpStatusCode.BadRequest, (await browser.GetAsync("/health/live")).StatusCode);
    }

    [TestMethod]
    public async Task LandingAndHealthArePublicButRecordsRequireAuthentication()
    {
        await using var app = new WebFactory();
        using var browser = app.Browser(false);
        var landing = await browser.GetStringAsync("/");
        StringAssert.Contains(landing, "Your campus.");
        StringAssert.Contains(landing, "Skip to content");
        foreach (var path in new[] { "/health/live", "/health/ready" })
        {
            Assert.AreEqual(HttpStatusCode.OK, (await browser.GetAsync(path)).StatusCode);
        }
        foreach (var path in new[] { "/Students?id=7", "/Catalog", "/Enrollments", "/Enrollments/Create" })
        {
            Assert.AreEqual(HttpStatusCode.Unauthorized, (await browser.GetAsync(path)).StatusCode);
        }
        Assert.IsEmpty(app.Handler.Requests);
    }

    [TestMethod]
    public async Task StudentLookupUsesDelegatedTokenAndEncodesUntrustedData()
    {
        await using var app = new WebFactory();
        app.Handler.Respond = _ => ControlledHandler.Json("""{"id":7,"firstName":"<script>alert(1)</script>","lastName":"Smith","enrollmentDate":"2024-09-01T00:00:00Z"}""");
        using var browser = app.Browser();
        var response = await browser.GetAsync("/Students?id=7");
        var html = await response.Content.ReadAsStringAsync();
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        StringAssert.Contains(html, "&lt;script&gt;");
        Assert.DoesNotContain("<script>", html);
        Assert.AreEqual("Bearer controlled-test-token", app.Handler.Requests.Single().Authorization);
        Assert.AreEqual("/api/v1/students/7", app.Handler.Requests.Single().Uri.AbsolutePath);
        CollectionAssert.AreEqual(new[] { "api://test-students/students.read" }, app.Tokens.Requests.Single());
        Assert.IsTrue(response.Headers.CacheControl?.NoStore);
    }

    [TestMethod]
    [DataRow(401, "Sign-in required")]
    [DataRow(403, "Access denied")]
    [DataRow(404, "Record not found")]
    [DataRow(500, "Service unavailable")]
    [DataRow(503, "Service unavailable")]
    public async Task UpstreamFailureIsVisibleWithoutLeakingBody(int status, string message)
    {
        await using var app = new WebFactory();
        app.Handler.Respond = _ => ControlledHandler.Json("""{"error":"SECRET SQL password trace"}""", (HttpStatusCode)status);
        using var browser = app.Browser();
        var response = await browser.GetAsync("/Students?id=7");
        var html = await response.Content.ReadAsStringAsync();
        Assert.AreEqual(status >= 500 ? 503 : status, (int)response.StatusCode);
        StringAssert.Contains(html, message);
        StringAssert.Contains(html, "Support reference:");
        Assert.DoesNotContain("SECRET SQL", html);
    }

    [TestMethod]
    public async Task MalformedRecordsAndNetworkFailuresAreNotEmptySuccesses()
    {
        await using var app = new WebFactory();
        using var browser = app.Browser();
        app.Handler.Respond = _ => ControlledHandler.Json("""{"id":7,"firstName":"Ada"}""");
        var response = await browser.GetAsync("/Students?id=7");
        Assert.AreEqual(HttpStatusCode.BadGateway, response.StatusCode);
        StringAssert.Contains(await response.Content.ReadAsStringAsync(), "Service unavailable");
        app.Handler.Respond = _ => throw new HttpRequestException("private host");
        response = await browser.GetAsync("/Students?id=7");
        Assert.AreEqual(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.DoesNotContain("private host", await response.Content.ReadAsStringAsync());
    }

    [TestMethod]
    public async Task InvalidIdsAndPagesNeverReachServices()
    {
        await using var app = new WebFactory();
        using var browser = app.Browser();
        foreach (var path in new[] { "/Students?id=0", "/Students?id=abc", "/Catalog?page=-1", "/Catalog/Details/0", "/Enrollments?page=100001", "/Enrollments/Delete/0" })
        {
            Assert.AreEqual(HttpStatusCode.BadRequest, (await browser.GetAsync(path)).StatusCode, path);
        }
        Assert.IsEmpty(app.Handler.Requests);
    }

    [TestMethod]
    public async Task CatalogAndEnrollmentsRenderActualPagedSchemas()
    {
        await using var app = new WebFactory();
        using var browser = app.Browser();
        app.Handler.Respond = request => ControlledHandler.Json(request.Uri.Host.StartsWith("academics", StringComparison.Ordinal)
            ? """{"courses":[{"id":9,"title":"Applied mathematics","credits":4,"departmentId":2,"department":{"id":2,"name":"Science"}}],"count":51,"pages":2,"currentPage":1}"""
            : """{"items":[{"id":3,"studentId":7,"courseId":9,"grade":null}],"page":1,"pageSize":50,"totalCount":51}""");
        var catalog = await browser.GetStringAsync("/Catalog");
        StringAssert.Contains(catalog, "Applied mathematics");
        StringAssert.Contains(catalog, "Science");
        StringAssert.Contains(catalog, "page=2");
        var enrollment = await browser.GetStringAsync("/Enrollments");
        StringAssert.Contains(enrollment, "Ungraded");
        StringAssert.Contains(enrollment, "page=2");
        Assert.IsTrue(app.Handler.Requests.All(request => request.Uri.Query == "?page=1"));
        CollectionAssert.AreEqual(new[] { "api://test-academics/academics.read" }, app.Tokens.Requests[0]);
        CollectionAssert.AreEqual(new[] { "api://test-enrollments/enrollments.read" }, app.Tokens.Requests[1]);
    }

    [TestMethod]
    public async Task EnrollmentCreationRequiresCsrfAndSendsOnlyPublishedDto()
    {
        await using var app = new WebFactory();
        using var browser = app.Browser();
        var missing = await browser.PostAsync("/Enrollments/Create", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["StudentId"] = "7",
            ["CourseId"] = "9"
        }));
        Assert.AreEqual(HttpStatusCode.BadRequest, missing.StatusCode);
        Assert.IsEmpty(app.Handler.Requests);
        var page = await browser.GetAsync("/Enrollments/Create?courseId=9");
        var token = Token(await page.Content.ReadAsStringAsync());
        var cookie = page.Headers.GetValues("Set-Cookie").Single(value => value.StartsWith("__Host-Contoso-CSRF", StringComparison.Ordinal));
        StringAssert.Contains(cookie, "secure");
        StringAssert.Contains(cookie, "httponly");
        StringAssert.Contains(cookie, "samesite=strict");
        app.Handler.Respond = _ => ControlledHandler.Json("""{"id":3,"studentId":7,"courseId":9,"grade":null}""", HttpStatusCode.Created);
        var result = await browser.PostAsync("/Enrollments/Create", Form(token, ("StudentId", "7"), ("CourseId", "9"), ("Grade", ""), ("Roles", "Registrar")));
        Assert.AreEqual(HttpStatusCode.Redirect, result.StatusCode);
        var sent = app.Handler.Requests.Single();
        Assert.AreEqual("POST", sent.Method);
        using var json = JsonDocument.Parse(sent.Body!);
        CollectionAssert.AreEquivalent(new[] { "studentId", "courseId", "grade" }, json.RootElement.EnumerateObject().Select(property => property.Name).ToArray());
        Assert.AreEqual(JsonValueKind.Null, json.RootElement.GetProperty("grade").ValueKind);
        CollectionAssert.AreEqual(new[] { "api://test-enrollments/enrollments.read", "api://test-enrollments/enrollments.write" }, app.Tokens.Requests.Single());
    }

    [TestMethod]
    public async Task InvalidEnrollmentAndGradeAreRejectedBeforeHttp()
    {
        await using var app = new WebFactory();
        using var browser = app.Browser();
        var token = Token(await browser.GetStringAsync("/Enrollments/Create"));
        foreach (var values in new[] { new[] { ("StudentId", "0"), ("CourseId", "9") }, new[] { ("StudentId", "7"), ("CourseId", "9"), ("Grade", "E") } })
        {
            var response = await browser.PostAsync("/Enrollments/Create", Form(token, values));
            Assert.AreEqual(HttpStatusCode.BadRequest, response.StatusCode);
            StringAssert.Contains(await response.Content.ReadAsStringAsync(), "validation");
        }
        var invalidEdit = await browser.PostAsync("/Enrollments/Edit/3", Form(token, ("Grade", "E")));
        Assert.AreEqual(HttpStatusCode.BadRequest, invalidEdit.StatusCode);
        Assert.IsEmpty(app.Handler.Requests);
    }

    [TestMethod]
    [DataRow("/Enrollments", "{\"items\":[{\"id\":3,\"studentId\":7,\"courseId\":9}],\"page\":1,\"pageSize\":50,\"totalCount\":1}")]
    [DataRow("/Enrollments", "{\"items\":[null],\"page\":1,\"pageSize\":50,\"totalCount\":1}")]
    [DataRow("/Enrollments", "{\"items\":[{\"id\":3,\"studentId\":7,\"courseId\":9,\"grade\":\"Z\"}],\"page\":1,\"pageSize\":50,\"totalCount\":1}")]
    [DataRow("/Catalog", "{\"courses\":[null],\"count\":1,\"pages\":1,\"currentPage\":1}")]
    public async Task ContractViolationsAreVisibleFailures(string path, string json)
    {
        await using var app = new WebFactory();
        using var browser = app.Browser();
        app.Handler.Respond = _ => ControlledHandler.Json(json);
        var response = await browser.GetAsync(path);
        Assert.AreEqual(HttpStatusCode.BadGateway, response.StatusCode);
        StringAssert.Contains(await response.Content.ReadAsStringAsync(), "Service unavailable");
    }

    [TestMethod]
    public async Task PostedRegistrarRoleCannotBypassBackendDenial()
    {
        await using var app = new WebFactory();
        using var browser = app.Browser();
        var token = Token(await browser.GetStringAsync("/Enrollments/Create"));
        app.Handler.Respond = _ => new HttpResponseMessage(HttpStatusCode.Forbidden);
        var response = await browser.PostAsync("/Enrollments/Create",
            Form(token, ("StudentId", "7"), ("CourseId", "9"), ("Role", "Registrar")));
        Assert.AreEqual(HttpStatusCode.Forbidden, response.StatusCode);
        StringAssert.Contains(await response.Content.ReadAsStringAsync(), "Access denied");
    }

    [TestMethod]
    public async Task GradeUpdateAndConfirmedDeleteUseCorrectMethodsAndScopedTokens()
    {
        await using var app = new WebFactory();
        using var browser = app.Browser();
        app.Handler.Respond = request => request.Method == "GET"
            ? ControlledHandler.Json("""{"id":3,"studentId":7,"courseId":9,"grade":"B"}""")
            : new HttpResponseMessage(HttpStatusCode.NoContent);
        var token = Token(await browser.GetStringAsync("/Enrollments/Edit/3"));
        var updated = await browser.PostAsync("/Enrollments/Edit/3", Form(token, ("Grade", "A"), ("StudentId", "999")));
        Assert.AreEqual(HttpStatusCode.Redirect, updated.StatusCode);
        Assert.AreEqual("""{"grade":"A"}""", app.Handler.Requests[1].Body);
        Assert.AreEqual("PUT", app.Handler.Requests[1].Method);
        var confirmation = await browser.GetStringAsync("/Enrollments/Delete/3");
        StringAssert.Contains(confirmation, "This cannot be undone.");
        var deleted = await browser.PostAsync("/Enrollments/Delete/3", Form(Token(confirmation)));
        Assert.AreEqual(HttpStatusCode.Redirect, deleted.StatusCode);
        Assert.AreEqual("DELETE", app.Handler.Requests.Last().Method);
        Assert.IsNull(app.Handler.Requests.Last().Body);
        CollectionAssert.AreEqual(new[] { "api://test-enrollments/enrollments.read", "api://test-enrollments/enrollments.write" }, app.Tokens.Requests.Last());
    }

    [TestMethod]
    public async Task RealIdentityOptionsAreConfidentialSecretlessAndCookieProtected()
    {
        await using var app = new WebFactory();
        using var browser = app.Browser();
        await browser.GetAsync("/");
        var cookie = app.Services.GetRequiredService<IOptionsMonitor<CookieAuthenticationOptions>>().Get(CookieAuthenticationDefaults.AuthenticationScheme);
        Assert.IsTrue(cookie.Cookie.HttpOnly);
        Assert.AreEqual(CookieSecurePolicy.Always, cookie.Cookie.SecurePolicy);
        Assert.AreEqual("__Host-Contoso", cookie.Cookie.Name);
        var oidc = app.Services.GetRequiredService<IOptionsMonitor<OpenIdConnectOptions>>().Get(OpenIdConnectDefaults.AuthenticationScheme);
        Assert.IsFalse(oidc.SaveTokens);
        Assert.IsTrue(oidc.UsePkce);
        Assert.AreEqual("code", oidc.ResponseType);
        var identity = app.Services.GetRequiredService<IOptionsMonitor<MicrosoftIdentityOptions>>().Get(OpenIdConnectDefaults.AuthenticationScheme);
        Assert.AreEqual(CredentialSource.SignedAssertionFromManagedIdentity, identity.ClientCredentials!.Single().SourceType);
        Assert.IsTrue(string.IsNullOrEmpty(identity.ClientCredentials!.Single().ManagedIdentityClientId));
        Assert.IsTrue(string.IsNullOrEmpty(identity.ClientSecret));
    }

    private static string Token(string html) => WebUtility.HtmlDecode(
        Regex.Match(html, "name=\"__RequestVerificationToken\" type=\"hidden\" value=\"([^\"]+)\"", RegexOptions.CultureInvariant).Groups[1].Value);

    private static FormUrlEncodedContent Form(string token, params (string Key, string Value)[] values) =>
        new(values.Append((Key: "__RequestVerificationToken", Value: token)).Select(value => new KeyValuePair<string, string>(value.Key, value.Value)));
}

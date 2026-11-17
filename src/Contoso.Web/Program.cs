using Azure.Identity;
using Azure.Monitor.OpenTelemetry.AspNetCore;
using Contoso.Web.Services;
using Contoso.Web.Controllers;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Identity.Web;
using OpenTelemetry.Resources;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddControllersWithViews(options =>
{
    options.Filters.Add(new AutoValidateAntiforgeryTokenAttribute());
    options.Filters.Add<BackendErrorFilter>();
});
builder.Services.AddAuthentication(OpenIdConnectDefaults.AuthenticationScheme)
    .AddMicrosoftIdentityWebApp(builder.Configuration.GetSection("AzureAd"))
    .EnableTokenAcquisitionToCallDownstreamApi()
    .AddInMemoryTokenCaches();
builder.Services.Configure<OpenIdConnectOptions>(OpenIdConnectDefaults.AuthenticationScheme, options =>
{
    options.ResponseType = "code";
    options.UsePkce = true;
    options.SaveTokens = false;
    options.Events.OnRemoteFailure = context =>
    {
        context.HandleResponse();
        context.Response.Redirect("/account/error");
        return Task.CompletedTask;
    };
});
builder.Services.Configure<CookieAuthenticationOptions>(CookieAuthenticationDefaults.AuthenticationScheme, options =>
{
    options.Cookie.Name = "__Host-Contoso";
    options.Cookie.HttpOnly = true;
    options.Cookie.SecurePolicy = CookieSecurePolicy.Always;
    options.Cookie.SameSite = SameSiteMode.Lax;
    options.ExpireTimeSpan = TimeSpan.FromHours(1);
    options.SlidingExpiration = false;
});
builder.Services.AddAntiforgery(options =>
{
    options.Cookie.Name = "__Host-Contoso-CSRF";
    options.Cookie.SecurePolicy = CookieSecurePolicy.Always;
    options.Cookie.HttpOnly = true;
    options.Cookie.SameSite = SameSiteMode.Strict;
});
var protection = builder.Services.AddDataProtection().SetApplicationName("contoso-web");
if (builder.Configuration["DataProtection:KeyPath"] is { Length: > 0 } keyPath)
{
    protection.PersistKeysToFileSystem(new DirectoryInfo(keyPath));
}
builder.Services.AddHttpContextAccessor();
builder.Services.AddScoped<IDelegatedTokens, DelegatedTokens>();
builder.Services.AddScoped<BackendTransport>();
builder.Services.AddScoped<StudentsApi>();
builder.Services.AddScoped<AcademicsApi>();
builder.Services.AddScoped<EnrollmentsApi>();
foreach (var name in ServiceConfiguration.Names)
{
    builder.Services.AddHttpClient(name, client =>
    {
        client.BaseAddress = new Uri(builder.Configuration[$"Services:{name}:BaseUrl"]!);
        client.Timeout = TimeSpan.FromSeconds(10);
    }).ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false });
}
if (!builder.Environment.IsDevelopment() && !builder.Environment.IsEnvironment("Testing"))
{
    builder.Services.AddOpenTelemetry()
        .ConfigureResource(resource => resource.AddService("contoso-web"))
        .UseAzureMonitor(options =>
        {
            options.ConnectionString = builder.Configuration["APPLICATIONINSIGHTS_CONNECTION_STRING"];
            options.Credential = new DefaultAzureCredential();
        });
}
var app = builder.Build();
ServiceConfiguration.Validate(app.Configuration, app.Environment);
app.UseExceptionHandler("/home/error");
if (!app.Environment.IsDevelopment() && !app.Environment.IsEnvironment("Testing"))
{
    app.UseHsts();
}
app.UseHttpsRedirection();
app.Use(async (context, next) =>
{
    context.Response.Headers.ContentSecurityPolicy = "default-src 'self'; style-src 'self'; script-src 'none'; frame-ancestors 'none'; base-uri 'self'; form-action 'self'";
    context.Response.Headers.XContentTypeOptions = "nosniff";
    context.Response.Headers["Referrer-Policy"] = "no-referrer";
    if (!context.Request.Path.StartsWithSegments("/css"))
    {
        context.Response.Headers.CacheControl = "no-store";
    }
    await next();
});
app.UseStaticFiles();
app.UseRouting();
app.UseAuthentication();
app.UseAuthorization();
app.MapGet("/health/live", () => Results.Ok(new { status = "live" })).AllowAnonymous();
// Configuration readiness only: no user tokens, backend fan-out or dependency credentials.
app.MapGet("/health/ready", () => Results.Ok(new { status = "ready", scope = "web-configuration" })).AllowAnonymous();
app.MapControllerRoute("default", "{controller=Home}/{action=Index}/{id?}");
app.Run();

public partial class Program;

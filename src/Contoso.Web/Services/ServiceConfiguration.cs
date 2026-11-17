namespace Contoso.Web.Services;

public static class ServiceConfiguration
{
    public static readonly string[] Names = ["Students", "Academics", "Enrollments"];

    public static void Validate(IConfiguration config, IHostEnvironment environment)
    {
        if (config.GetValue<bool>("ASPNETCORE_FORWARDEDHEADERS_ENABLED")
            || config.GetValue<bool>("FORWARDEDHEADERS_ENABLED"))
        {
            throw new InvalidOperationException("Trust-all forwarded headers are unsupported; configure the explicit hosting boundary.");
        }
        var appServiceHost = config["WEBSITE_HOSTNAME"];
        var appServiceHttpsOnly = config.GetValue<bool>("Hosting:AppServiceHttpsOnly");
        if (appServiceHttpsOnly || !string.IsNullOrEmpty(appServiceHost))
        {
            var hosts = config["AllowedHosts"]?.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries) ?? [];
            if (!appServiceHttpsOnly || string.IsNullOrWhiteSpace(config["WEBSITE_SITE_NAME"])
                || Uri.CheckHostName(appServiceHost ?? "") != UriHostNameType.Dns
                || hosts.Length != 1 || !string.Equals(hosts[0], appServiceHost, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("App Service requires the HTTPS-only platform boundary, native site/hostname metadata and that exact AllowedHosts value.");
            }
        }
        foreach (var key in new[] { "AzureAd:TenantId", "AzureAd:ClientId" })
        {
            if (!Guid.TryParse(config[key], out var value) || value == Guid.Empty)
            {
                throw new InvalidOperationException($"Required configuration {key} must be a nonempty GUID.");
            }
        }
        if (config["AzureAd:ClientCredentials:0:SourceType"] != "SignedAssertionFromManagedIdentity"
            || config.GetSection("AzureAd:ClientCredentials").GetChildren().Count() != 1
            || !string.IsNullOrEmpty(config["AzureAd:ClientSecret"])
            || config.GetSection("AzureAd:ClientCertificates").GetChildren().Any())
        {
            throw new InvalidOperationException("Configure only the managed-identity federated client credential.");
        }
        foreach (var name in Names)
        {
            var section = config.GetSection($"Services:{name}");
            if (!Uri.TryCreate(section["BaseUrl"], UriKind.Absolute, out var uri)
                || uri.Scheme != "https" || uri.AbsolutePath != "/" || uri.UserInfo.Length != 0
                || uri.Query.Length != 0 || uri.Fragment.Length != 0)
            {
                throw new InvalidOperationException($"Services:{name}:BaseUrl must be an HTTPS origin.");
            }
            var read = section.GetSection("ReadScopes").Get<string[]>() ?? [];
            var write = section.GetSection("WriteScopes").Get<string[]>() ?? [];
            var suffix = "/" + name.ToLowerInvariant() + ".read";
            if (read.Length != 1 || !read[0].StartsWith("api://", StringComparison.Ordinal)
                || read[0].Any(char.IsWhiteSpace)
                || !Uri.TryCreate(read[0], UriKind.Absolute, out var scopeUri)
                || scopeUri.Host.Length == 0 || scopeUri.Query.Length != 0 || scopeUri.Fragment.Length != 0
                || !read[0].EndsWith(suffix, StringComparison.Ordinal))
            {
                throw new InvalidOperationException($"Services:{name}:ReadScopes requires exactly the resource's {name.ToLowerInvariant()}.read scope.");
            }
            if (name == "Enrollments" && (write.Length != 2 || !write.Contains(read[0], StringComparer.Ordinal)
                || !write.Contains(read[0][..^4] + "write", StringComparer.Ordinal)))
            {
                throw new InvalidOperationException($"Services:{name}:WriteScopes requires read and write scopes on the same resource.");
            }
        }
        if (!environment.IsDevelopment() && !environment.IsEnvironment("Testing"))
        {
            foreach (var key in new[] { "APPLICATIONINSIGHTS_CONNECTION_STRING", "DataProtection:KeyPath" })
            {
                if (string.IsNullOrWhiteSpace(config[key]))
                {
                    throw new InvalidOperationException($"Required configuration {key} is missing.");
                }
            }
            var hosts = config["AllowedHosts"]?.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries) ?? [];
            if (hosts.Length == 0 || hosts.Any(host => host.Contains('*') || Uri.CheckHostName(host) != UriHostNameType.Dns))
            {
                throw new InvalidOperationException("AllowedHosts must list the exact deployed Web DNS hostname(s), without wildcards.");
            }
        }
    }
}

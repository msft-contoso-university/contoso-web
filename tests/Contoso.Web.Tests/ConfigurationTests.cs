using Contoso.Web.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.FileProviders;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Contoso.Web.Tests;

[TestClass]
public sealed class ConfigurationTests
{
    [TestMethod]
    [DataRow("Services:Students:BaseUrl", "http://students.example.test/")]
    [DataRow("Services:Students:BaseUrl", "https://students.example.test/override")]
    [DataRow("Services:Students:ReadScopes:0", "api://test-students/access_as_user")]
    [DataRow("Services:Enrollments:WriteScopes:1", "api://test-academics/enrollments.write")]
    [DataRow("AzureAd:ClientCredentials:0:SourceType", "ClientSecret")]
    [DataRow("AzureAd:ClientSecret", "not-a-real-secret")]
    [DataRow("AzureAd:TenantId", "common")]
    public void UnsafeOrMismatchedConfigurationFailsBeforeServing(string key, string value)
    {
        var values = WebFactory.Configuration();
        values[key] = value;
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(values).Build();
        Assert.ThrowsExactly<InvalidOperationException>(() => ServiceConfiguration.Validate(configuration, new EnvironmentStub("Testing")));
    }

    [TestMethod]
    public void ProductionRequiresTelemetryAndPersistentCookieKeyConfiguration()
    {
        var values = WebFactory.Configuration();
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(values).Build();
        Assert.ThrowsExactly<InvalidOperationException>(() => ServiceConfiguration.Validate(configuration, new EnvironmentStub("Production")));
        values["APPLICATIONINSIGHTS_CONNECTION_STRING"] = "InstrumentationKey=00000000-0000-0000-0000-000000000001";
        configuration = new ConfigurationBuilder().AddInMemoryCollection(values).Build();
        Assert.ThrowsExactly<InvalidOperationException>(() => ServiceConfiguration.Validate(configuration, new EnvironmentStub("Production")));
        values["DataProtection:KeyPath"] = "configured-platform-mount";
        configuration = new ConfigurationBuilder().AddInMemoryCollection(values).Build();
        Assert.ThrowsExactly<InvalidOperationException>(() => ServiceConfiguration.Validate(configuration, new EnvironmentStub("Production")));
        values["AllowedHosts"] = "web.example.test";
        configuration = new ConfigurationBuilder().AddInMemoryCollection(values).Build();
        ServiceConfiguration.Validate(configuration, new EnvironmentStub("Production"));
    }

    [TestMethod]
    [DataRow("Hosting:AppServiceHttpsOnly", "false")]
    [DataRow("WEBSITE_SITE_NAME", "")]
    [DataRow("WEBSITE_HOSTNAME", "")]
    [DataRow("AllowedHosts", "*")]
    [DataRow("AllowedHosts", "another.example.test")]
    [DataRow("ASPNETCORE_FORWARDEDHEADERS_ENABLED", "true")]
    [DataRow("FORWARDEDHEADERS_ENABLED", "true")]
    public void AppServiceRequiresAnExplicitHttpsBoundaryAndExactHost(string key, string value)
    {
        var values = WebFactory.Configuration();
        values["Hosting:AppServiceHttpsOnly"] = "true";
        values["WEBSITE_SITE_NAME"] = "web";
        values["WEBSITE_HOSTNAME"] = "web.example.test";
        values["AllowedHosts"] = "web.example.test";
        values[key] = value;
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(values).Build();
        Assert.ThrowsExactly<InvalidOperationException>(() => ServiceConfiguration.Validate(configuration, new EnvironmentStub("Testing")));
    }

    private sealed class EnvironmentStub(string name) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = name;
        public string ApplicationName { get; set; } = "Contoso.Web.Tests";
        public string ContentRootPath { get; set; } = "";
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}

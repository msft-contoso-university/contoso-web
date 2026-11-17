using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Identity.Web;

namespace Contoso.Web.Services;

public interface IDelegatedTokens
{
    Task<string> GetAsync(string[] scopes);
}

public sealed class DelegatedTokens(ITokenAcquisition acquisition, IHttpContextAccessor context) : IDelegatedTokens
{
    public Task<string> GetAsync(string[] scopes) =>
        acquisition.GetAccessTokenForUserAsync(scopes, user: context.HttpContext!.User);
}

public sealed class BackendException(int statusCode) : Exception("The backend request could not be completed.")
{
    public int StatusCode { get; } = statusCode;
}

public sealed class BackendTransport(IHttpClientFactory factory, IDelegatedTokens tokens, IConfiguration configuration)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        RespectRequiredConstructorParameters = true,
        NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.Strict
    };

    public async Task<T> ReadAsync<T>(string service, string path, CancellationToken cancellationToken)
    {
        using var response = await SendAsync(service, HttpMethod.Get, path, null, cancellationToken);
        if (response.StatusCode != HttpStatusCode.OK)
        {
            throw new BackendException(502);
        }
        return await DeserializeAsync<T>(response, cancellationToken);
    }

    public async Task<T> CreateAsync<T>(string service, string path, object input, CancellationToken cancellationToken)
    {
        using var response = await SendAsync(service, HttpMethod.Post, path, input, cancellationToken);
        if (response.StatusCode != HttpStatusCode.Created)
        {
            throw new BackendException(502);
        }
        return await DeserializeAsync<T>(response, cancellationToken);
    }

    public async Task MutateAsync(string service, HttpMethod method, string path, object? input, CancellationToken cancellationToken)
    {
        using var response = await SendAsync(service, method, path, input, cancellationToken);
        if (response.StatusCode != HttpStatusCode.NoContent)
        {
            throw new BackendException(502);
        }
    }

    private async Task<HttpResponseMessage> SendAsync(string service, HttpMethod method, string path, object? input, CancellationToken cancellationToken)
    {
        var scopes = configuration.GetSection($"Services:{service}:{(method == HttpMethod.Get ? "ReadScopes" : "WriteScopes")}").Get<string[]>()!;
        // Token acquisition exceptions deliberately reach AuthorizeForScopes for per-resource incremental consent.
        var token = await tokens.GetAsync(scopes);
        using var request = new HttpRequestMessage(method, path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        if (input is not null)
        {
            request.Content = JsonContent.Create(input);
        }
        try
        {
            var response = await factory.CreateClient(service).SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                var status = (int)response.StatusCode;
                response.Dispose();
                throw new BackendException(status is 400 or 401 or 403 or 404 or 409 ? status : 503);
            }
            return response;
        }
        catch (HttpRequestException)
        {
            throw new BackendException(503);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new BackendException(503);
        }
    }

    private static async Task<T> DeserializeAsync<T>(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(10));
            await response.Content.LoadIntoBufferAsync(1_048_576, timeout.Token);
            return await response.Content.ReadFromJsonAsync<T>(JsonOptions, timeout.Token)
                ?? throw new BackendException(502);
        }
        catch (Exception exception) when (exception is JsonException or HttpRequestException or NotSupportedException)
        {
            throw new BackendException(502);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new BackendException(503);
        }
    }
}

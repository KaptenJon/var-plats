namespace VarPlats.Web;

internal sealed class GitHubUserAgentHandler() : DelegatingHandler(new HttpClientHandler())
{
    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (request.RequestUri?.Host == "api.github.com")
            request.Headers.UserAgent.ParseAdd("VarPlats/1.0");
        return base.SendAsync(request, cancellationToken);
    }
}

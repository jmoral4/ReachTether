internal sealed class OpenAiResponsesClient(HttpClient httpClient) : IDisposable
{
    public HttpClient HttpClient { get; } = httpClient;

    public void Dispose()
    {
        HttpClient.Dispose();
    }
}

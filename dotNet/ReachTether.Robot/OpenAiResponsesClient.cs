internal sealed class OpenAiResponsesClient(HttpClient httpClient)
{
    public HttpClient HttpClient { get; } = httpClient;
}

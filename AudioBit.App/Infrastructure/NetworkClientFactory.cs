using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.WebSockets;

namespace AudioBit.App.Infrastructure;

internal static class NetworkClientFactory
{
    private const string UserAgentValue = "AudioBit/1.0";

    public static HttpClient CreateHttpClient(
        TimeSpan timeout,
        bool allowAutoRedirect = true,
        string? acceptHeader = null)
    {
        var client = new HttpClient(CreateHttpHandler(allowAutoRedirect), disposeHandler: true)
        {
            Timeout = timeout,
        };

        client.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgentValue);
        if (!string.IsNullOrWhiteSpace(acceptHeader))
        {
            client.DefaultRequestHeaders.Accept.Clear();
            client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue(acceptHeader));
        }

        return client;
    }

    public static ClientWebSocket CreateWebSocket()
    {
        var socket = new ClientWebSocket();
        socket.Options.KeepAliveInterval = TimeSpan.FromSeconds(20);

        try
        {
            socket.Options.Proxy = WebRequest.DefaultWebProxy;
        }
        catch
        {
            
        }

        try
        {
            socket.Options.SetRequestHeader("User-Agent", UserAgentValue);
        }
        catch
        {
            
        }

        return socket;
    }

    private static HttpMessageHandler CreateHttpHandler(bool allowAutoRedirect)
    {
        return new SocketsHttpHandler
        {
            AllowAutoRedirect = allowAutoRedirect,
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate | DecompressionMethods.Brotli,
            ConnectTimeout = TimeSpan.FromSeconds(8),
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            UseCookies = false,
            UseProxy = true,
            Proxy = WebRequest.DefaultWebProxy,
        };
    }
}

namespace Plicbox.Dav;

/// <summary>Telling "Google couldn't be reached" (keep the change and send it later) from "Google said no".</summary>
internal static class Connectivity
{
    /// <summary>
    /// No answer at all: no network, the name didn't resolve, the connection dropped or timed out. An answer with a
    /// status (400, 404…) carries its code, so it isn't taken for this.
    /// </summary>
    public static bool IsOffline(Exception ex, CancellationToken ct) => ex switch
    {
        HttpRequestException { StatusCode: null } => true,
        TaskCanceledException or TimeoutException => !ct.IsCancellationRequested, // HttpClient's timeout, not the user's cancel
        System.Net.Sockets.SocketException or IOException => true,
        _ => ex.InnerException is { } inner && IsOffline(inner, ct),
    };

    /// <summary>Google answered that the thing is gone (deleted elsewhere meanwhile).</summary>
    public static bool IsGone(Exception ex) =>
        ex is HttpRequestException { StatusCode: System.Net.HttpStatusCode.NotFound or System.Net.HttpStatusCode.Gone };

    /// <summary>A server-side hiccup (500s, too many requests): worth trying again later.</summary>
    public static bool IsTemporary(Exception ex) =>
        ex is HttpRequestException { StatusCode: { } code } && ((int)code >= 500 || code == System.Net.HttpStatusCode.TooManyRequests);
}

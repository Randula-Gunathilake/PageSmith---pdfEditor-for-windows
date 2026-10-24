using System.Net.Http;
using System.Net.NetworkInformation;

namespace PageSmith.Core.Net;

/// <summary>Raised when a feature needs the internet and the PC is offline.</summary>
public sealed class OfflineException(string feature, Exception? inner = null)
    : UserFacingException($"{feature} needs an internet connection, and this PC appears to be offline. Connect to the internet and try again.", inner)
{
    public string Feature { get; } = feature;
}

/// <summary>Checks whether the internet is reachable, the way Windows' own network indicator does.</summary>
public static class Connectivity
{
    private static readonly HttpClient Probe = new() { Timeout = TimeSpan.FromSeconds(5) };

    /// <summary>Raised (on a background thread) when a network adapter connects or disconnects.</summary>
    public static event EventHandler<bool>? NetworkAvailabilityChanged;

    static Connectivity()
    {
        NetworkChange.NetworkAvailabilityChanged += (_, e) => NetworkAvailabilityChanged?.Invoke(null, e.IsAvailable);
    }

    /// <summary>Fast local check: is any network adapter connected?</summary>
    public static bool IsNetworkAvailable => NetworkInterface.GetIsNetworkAvailable();

    /// <summary>
    /// True when the internet answers: Windows' connectivity-test endpoint first (what NCSI uses),
    /// then a well-known 204 endpoint. Returns quickly when no adapter is connected.
    /// </summary>
    public static async Task<bool> HasInternetAsync(CancellationToken ct = default)
    {
        if (!IsNetworkAvailable) return false;
        foreach (var (url, check) in new (string, Func<HttpResponseMessage, Task<bool>>)[]
                 {
                     ("http://www.msftconnecttest.com/connecttest.txt", async r => r.IsSuccessStatusCode && (await r.Content.ReadAsStringAsync(ct)).Contains("Microsoft Connect Test")),
                     ("https://www.google.com/generate_204", r => Task.FromResult((int)r.StatusCode == 204)),
                 })
        {
            try
            {
                using var response = await Probe.GetAsync(url, ct).ConfigureAwait(false);
                if (await check(response).ConfigureAwait(false)) return true;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch
            {
                // Try the next endpoint.
            }
        }
        return false;
    }

    /// <summary>Throws <see cref="OfflineException"/> when the internet is not reachable.</summary>
    public static async Task EnsureOnlineAsync(string feature, CancellationToken ct = default)
    {
        if (!await HasInternetAsync(ct).ConfigureAwait(false)) throw new OfflineException(feature);
    }

    /// <summary>True for exceptions that mean "the network failed" rather than "the service said no".</summary>
    public static bool IsNetworkFailure(Exception ex)
    {
        for (var e = ex; e != null; e = e.InnerException)
        {
            if (e is HttpRequestException or System.Net.Sockets.SocketException or TaskCanceledException { CancellationToken.IsCancellationRequested: false })
                return true;
        }
        return false;
    }
}

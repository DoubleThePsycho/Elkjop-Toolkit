using System.Net.Http;
using System.Net.NetworkInformation;

namespace Support_Tool.Services;

// Read-only network state checks - safe to call in any build configuration, no dry-run split
// needed since nothing here changes system state.
internal static class ConnectivityService
{
    private static readonly Uri NcsiUri = new("http://www.msftconnecttest.com/connecttest.txt");
    private const string NcsiExpectedBody = "Microsoft Connect Test";

    // Same check Windows' own Network Connectivity Status Indicator uses internally - more
    // reliable than trusting an adapter's own "connected" status, which can be true even
    // when the access point itself has no working uplink.
    public static bool HasInternet()
    {
        try
        {
            // Task.Run, not a direct .GetAwaiter().GetResult() on the call below - this may be
            // called from a UI-thread poll timer running inside an active WPF dispatcher.
            // Blocking there directly on the async call would deadlock: HttpClient's
            // continuation tries to resume on the UI thread's SynchronizationContext, which is
            // exactly the thread sitting there blocked waiting for it. Task.Run hands the
            // awaited work to the thread pool instead, so its continuation never needs the UI
            // thread back.
            return Task.Run(async () =>
            {
                using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
                var body = await client.GetStringAsync(NcsiUri);
                return body.Trim() == NcsiExpectedBody;
            }).GetAwaiter().GetResult();
        }
        catch
        {
            return false;
        }
    }

    // True if Windows recognizes any Wi-Fi adapter at all, regardless of connection state.
    // Deliberately not parsing `netsh wlan show interfaces` text output - that's localized
    // based on whatever display language is active, while this BCL enumeration is not. No
    // adapter at all is the "missing driver" signal the Ethernet prompt hinges on.
    public static bool HasWifiAdapter()
    {
        return NetworkInterface.GetAllNetworkInterfaces()
            .Any(ni => ni.NetworkInterfaceType == NetworkInterfaceType.Wireless80211);
    }
}

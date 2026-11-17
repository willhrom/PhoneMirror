using System.Net.NetworkInformation;
using System.Net.Sockets;
using Windows.Devices.WiFiDirect;
using Windows.Security.Credentials;

namespace PhoneMirror;

/// <summary>
/// A private Wi-Fi Direct link from this laptop: phones running the Phone Mirror app can join it
/// when there's no shared Wi-Fi. No router, no hotspot setting, no internet shared.
/// </summary>
static class DirectLink
{
    static WiFiDirectAdvertisementPublisher? publisher;

    public static bool Running { get; private set; }
    public static string? Error { get; private set; }
    public static event Action? Changed;

    public static void Start(string ssid, string passphrase)
    {
        if (publisher != null) return;
        try
        {
            var p = new WiFiDirectAdvertisementPublisher();
            p.Advertisement.IsAutonomousGroupOwnerEnabled = true;
            p.Advertisement.ListenStateDiscoverability = WiFiDirectAdvertisementListenStateDiscoverability.Normal;
            p.Advertisement.LegacySettings.IsEnabled = true;
            p.Advertisement.LegacySettings.Ssid = ssid;
            p.Advertisement.LegacySettings.Passphrase = new PasswordCredential { Password = passphrase };
            p.StatusChanged += (_, e) =>
            {
                Running = e.Status == WiFiDirectAdvertisementPublisherStatus.Started;
                if (e.Status == WiFiDirectAdvertisementPublisherStatus.Aborted)
                {
                    Error = e.Error switch
                    {
                        WiFiDirectError.RadioNotAvailable => "Wi-Fi is turned off on this laptop.",
                        WiFiDirectError.ResourceInUse => "This laptop's Wi-Fi is busy (is Mobile hotspot on?).",
                        _ => "This laptop's Wi-Fi doesn't support direct connections.",
                    };
                    publisher = null;
                }
                Changed?.Invoke();
            };
            p.Start();
            publisher = p;
            Error = null;
        }
        catch (Exception ex)
        {
            Error = "Direct connections aren't available on this laptop (" + ex.Message + ").";
            publisher = null;
            Running = false;
            Changed?.Invoke();
        }
    }

    public static void Stop()
    {
        try { publisher?.Stop(); } catch { }
        publisher = null;
        Running = false;
        Changed?.Invoke();
    }

    /// <summary>Addresses a phone could have on the Wi-Fi Direct link (the small network Windows runs for it).</summary>
    public static IEnumerable<string> PeerAddresses()
    {
        foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (ni.OperationalStatus != OperationalStatus.Up) continue;
            if (!ni.Description.Contains("Wi-Fi Direct", StringComparison.OrdinalIgnoreCase)) continue;
            foreach (var ua in ni.GetIPProperties().UnicastAddresses)
            {
                if (ua.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                var b = ua.Address.GetAddressBytes();
                for (int i = 2; i < 255; i++)
                    if (i != b[3]) yield return $"{b[0]}.{b[1]}.{b[2]}.{i}";
            }
        }
    }
}

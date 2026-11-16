using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;

namespace PhoneMirror;

record MdnsService(string Instance, string Type, IPAddress Address, int Port)
{
    public string Endpoint => $"{Address}:{Port}";
}

/// <summary>
/// Tiny DNS-SD browser used to find phones for wireless pairing. Queries are sent from a
/// random port with the "unicast reply" bit set, so the phone answers us directly — that
/// works even when Windows Firewall blocks incoming multicast.
/// </summary>
static class Mdns
{
    static readonly IPEndPoint Group = new(IPAddress.Parse("224.0.0.251"), 5353);

    public static async Task<List<MdnsService>> BrowseAsync(string serviceType, int timeoutMs, CancellationToken ct)
    {
        var results = new List<MdnsService>();
        var query = BuildQuery(serviceType);
        var sockets = new List<UdpClient>();

        foreach (var ip in LocalIPv4())
        {
            try
            {
                var c = new UdpClient(new IPEndPoint(ip, 0));
                c.Client.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastInterface, ip.GetAddressBytes());
                c.Client.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastTimeToLive, 255);
                sockets.Add(c);
            }
            catch { }
        }

        // Also listen on 5353 for phones that answer by multicast anyway (works if the firewall allows it).
        UdpClient? shared = null;
        try
        {
            shared = new UdpClient(AddressFamily.InterNetwork);
            shared.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            shared.Client.Bind(new IPEndPoint(IPAddress.Any, 5353));
            foreach (var ip in LocalIPv4())
                try { shared.JoinMulticastGroup(Group.Address, ip); } catch { }
        }
        catch { shared?.Dispose(); shared = null; }

        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(timeoutMs);
            var receivers = sockets.Select(s => Receive(s, serviceType, results, cts.Token)).ToList();
            if (shared != null) receivers.Add(Receive(shared, serviceType, results, cts.Token));

            for (int i = 0; i < 2 && !cts.IsCancellationRequested; i++)
            {
                foreach (var s in sockets)
                    try { await s.SendAsync(query, query.Length, Group); } catch { }
                try { await Task.Delay(Math.Min(400, timeoutMs / 2), cts.Token); } catch { }
            }
            await Task.WhenAll(receivers);
        }
        finally
        {
            foreach (var s in sockets) s.Dispose();
            shared?.Dispose();
        }
        ct.ThrowIfCancellationRequested();
        return results;
    }

    static async Task Receive(UdpClient c, string serviceType, List<MdnsService> results, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            UdpReceiveResult r;
            try { r = await c.ReceiveAsync(ct); }
            catch { return; }
            try
            {
                foreach (var f in Parse(r.Buffer, r.RemoteEndPoint.Address))
                {
                    if (!f.Type.TrimEnd('.').Equals(serviceType.TrimEnd('.'), StringComparison.OrdinalIgnoreCase)) continue;
                    lock (results)
                        if (!results.Any(x => x.Instance == f.Instance && x.Port == f.Port))
                            results.Add(f);
                }
            }
            catch { }
        }
    }

    static IEnumerable<IPAddress> LocalIPv4()
    {
        foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (ni.OperationalStatus != OperationalStatus.Up || !ni.SupportsMulticast) continue;
            if (ni.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
            foreach (var ua in ni.GetIPProperties().UnicastAddresses)
                if (ua.Address.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(ua.Address))
                    yield return ua.Address;
        }
    }

    static byte[] BuildQuery(string name)
    {
        var b = new List<byte> { 0, 0, 0, 0, 0, 1, 0, 0, 0, 0, 0, 0 }; // id, flags, 1 question
        foreach (var label in name.TrimEnd('.').Split('.'))
        {
            var bytes = Encoding.UTF8.GetBytes(label);
            b.Add((byte)bytes.Length);
            b.AddRange(bytes);
        }
        b.Add(0);
        b.AddRange(new byte[] { 0, 12, 0x80, 1 }); // type PTR, class IN with unicast-response bit
        return b.ToArray();
    }

    static List<MdnsService> Parse(byte[] p, IPAddress from)
    {
        int U16(int o) => (p[o] << 8) | p[o + 1];
        var list = new List<MdnsService>();
        if (p.Length < 12) return list;
        int qd = U16(4), total = U16(6) + U16(8) + U16(10);
        int pos = 12;
        for (int i = 0; i < qd; i++) { ReadName(p, ref pos); pos += 4; }

        var srv = new List<(string Name, string Host, int Port)>();
        var a = new Dictionary<string, IPAddress>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < total && pos + 10 <= p.Length; i++)
        {
            var name = ReadName(p, ref pos);
            int type = U16(pos), rdlen = U16(pos + 8), rd = pos + 10;
            pos = rd + rdlen;
            if (pos > p.Length) break;
            if (type == 33 && rdlen >= 6)
            {
                int q = rd + 6;
                srv.Add((name, ReadName(p, ref q), U16(rd + 4)));
            }
            else if (type == 1 && rdlen == 4)
            {
                a[name] = new IPAddress(p.AsSpan(rd, 4).ToArray());
            }
        }

        foreach (var (name, host, port) in srv)
        {
            int dot = name.IndexOf('.');
            if (dot <= 0) continue;
            var instance = name[..dot];
            var type = name[(dot + 1)..];
            var addr = a.TryGetValue(host, out var ip) ? ip : from;
            if (addr.IsIPv4MappedToIPv6) addr = addr.MapToIPv4();
            list.Add(new MdnsService(instance, type, addr, port));
        }
        return list;
    }

    static string ReadName(byte[] p, ref int pos)
    {
        var labels = new List<string>();
        int cur = pos, jumps = 0;
        bool jumped = false;
        while (cur < p.Length)
        {
            int len = p[cur];
            if (len == 0) { cur++; break; }
            if ((len & 0xC0) == 0xC0)
            {
                if (cur + 1 >= p.Length) break;
                int ptr = ((len & 0x3F) << 8) | p[cur + 1];
                if (!jumped) pos = cur + 2;
                jumped = true;
                cur = ptr;
                if (++jumps > 30) break;
                continue;
            }
            if (cur + 1 + len > p.Length) break;
            labels.Add(Encoding.UTF8.GetString(p, cur + 1, len));
            cur += len + 1;
        }
        if (!jumped) pos = cur;
        return string.Join(".", labels);
    }
}

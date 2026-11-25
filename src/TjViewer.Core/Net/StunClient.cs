using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;

namespace TjViewer.Core.Net;

/// <summary>STUN 客户端（RFC 5389 Binding），获取公网映射地址用于 NAT 穿透（见 PRD FR-09）。</summary>
public static class StunClient
{
    private const uint MagicCookie = 0x2112A442;

    /// <summary>通过指定 UDP  socket 向 STUN 服务器查询公网映射端点。失败返回 null。</summary>
    public static async Task<IPEndPoint?> GetPublicEndpointAsync(UdpClient udp, string stunHost, int stunPort = 19302, int timeoutMs = 3000)
    {
        var tid = RandomNumberGenerator.GetBytes(12);
        var req = new byte[20];
        req[0] = 0x00; req[1] = 0x01; // Binding Request
        req[2] = 0x00; req[3] = 0x00; // length 0
        req[4] = 0x21; req[5] = 0x12; req[6] = 0xA4; req[7] = 0x42; // magic cookie
        Array.Copy(tid, 0, req, 8, 12);

        var server = new IPEndPoint(await ResolveAsync(stunHost), stunPort);
        await udp.SendAsync(req, req.Length, server);

        using var cts = new CancellationTokenSource(timeoutMs);
        try
        {
            var resp = await udp.ReceiveAsync(cts.Token);
            return ParseMappedAddress(resp.Buffer, tid);
        }
        catch (OperationCanceledException) { return null; }
        catch (SocketException) { return null; }
    }

    private static async Task<IPAddress> ResolveAsync(string host)
    {
        if (IPAddress.TryParse(host, out var ip)) return ip;
        var addrs = await Dns.GetHostAddressesAsync(host);
        return addrs.First(a => a.AddressFamily == AddressFamily.InterNetwork);
    }

    private static IPEndPoint? ParseMappedAddress(byte[] buf, byte[] tid)
    {
        if (buf.Length < 20) return null;
        int type = (buf[0] << 8) | buf[1];
        if (type != 0x0101) return null; // Binding Success Response
        for (int i = 0; i < 12; i++) if (buf[8 + i] != tid[i]) return null;
        int len = (buf[2] << 8) | buf[3];
        int pos = 20;
        while (pos + 4 <= 20 + len && pos + 4 <= buf.Length)
        {
            int attrType = (buf[pos] << 8) | buf[pos + 1];
            int attrLen = (buf[pos + 2] << 8) | buf[pos + 3];
            int val = pos + 4;
            if (attrType == 0x0020 && attrLen >= 8) // XOR-MAPPED-ADDRESS
            {
                int port = ((buf[val + 2] << 8) | buf[val + 3]) ^ 0x2112;
                var addrBytes = new byte[4];
                for (int i = 0; i < 4; i++) addrBytes[i] = (byte)(buf[val + 4 + i] ^ MagicCookieBytes[i]);
                return new IPEndPoint(new IPAddress(addrBytes), port);
            }
            if (attrType == 0x0001 && attrLen >= 8) // MAPPED-ADDRESS
            {
                int port = (buf[val + 2] << 8) | buf[val + 3];
                var addrBytes = new byte[4];
                Array.Copy(buf, val + 4, addrBytes, 0, 4);
                return new IPEndPoint(new IPAddress(addrBytes), port);
            }
            pos = val + ((attrLen + 3) & ~3); // 4 字节对齐
        }
        return null;
    }

    private static readonly byte[] MagicCookieBytes = { 0x21, 0x12, 0xA4, 0x42 };
}

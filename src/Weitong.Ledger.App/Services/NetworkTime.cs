using System.Net;
using System.Net.Sockets;

namespace Weitong.Ledger.App.Services;

/// <summary>
/// 轻量 SNTP 客户端：查询网络标准时间，用于检测本机系统时钟偏差。
/// 多端同步按 UpdatedAt(各机 UTC 时钟)裁决新旧，本机时钟偏差会导致「新改动被判为旧、不覆盖」，
/// 因此启动时对一次时，偏差过大就提醒用户开启系统自动对时。
/// UDP 123 被网络封锁时静默失败(返回 null)，不误报。
/// </summary>
public static class NetworkTime
{
    private static readonly string[] Servers = { "ntp.aliyun.com", "cn.pool.ntp.org", "time.windows.com" };

    /// <summary>查询网络 UTC 时间；任一服务器成功即返回，全部失败返回 null。</summary>
    public static DateTime? TryGetUtc(int timeoutMs = 3000)
    {
        foreach (var host in Servers)
        {
            try { return Query(host, timeoutMs); }
            catch { /* 换下一个服务器 */ }
        }
        return null;
    }

    private static DateTime Query(string host, int timeoutMs)
    {
        var packet = new byte[48];
        packet[0] = 0x1B;   // LI=0, VN=3, Mode=3(client)

        var ip = Array.Find(Dns.GetHostAddresses(host), a => a.AddressFamily == AddressFamily.InterNetwork)
                 ?? throw new SocketException();
        using var sock = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp)
        {
            ReceiveTimeout = timeoutMs,
            SendTimeout = timeoutMs,
        };
        sock.Connect(new IPEndPoint(ip, 123));
        sock.Send(packet);
        sock.Receive(packet);

        // Transmit Timestamp：字节 40..47（自 1900-01-01 UTC 的秒 + 小数秒）
        ulong seconds = ((ulong)packet[40] << 24) | ((ulong)packet[41] << 16) | ((ulong)packet[42] << 8) | packet[43];
        ulong fraction = ((ulong)packet[44] << 24) | ((ulong)packet[45] << 16) | ((ulong)packet[46] << 8) | packet[47];
        double ms = seconds * 1000d + fraction * 1000d / 0x100000000L;
        return new DateTime(1900, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddMilliseconds(ms);
    }
}

using System.Net.NetworkInformation;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32;

namespace Weitong.Ledger.App.Services;

/// <summary>
/// 机器码：本机稳定唯一标识，用作"这台机是谁"的身份锚点（替代登录）。
/// 优先取 Windows MachineGuid（每次安装稳定唯一）；取不到则回退到 机器名+网卡MAC 的哈希。
/// </summary>
public static class MachineId
{
    private static string? _cached;

    public static string Get()
    {
        if (_cached != null) return _cached;
        _cached = FromRegistry() ?? FromHardware();
        return _cached;
    }

    /// <summary>短展示码：取机器码前 8 位十六进制，便于人工核对。</summary>
    public static string Short() => Get()[..Math.Min(8, Get().Length)].ToUpperInvariant();

    private static string? FromRegistry()
    {
        try
        {
            using var key = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64)
                .OpenSubKey(@"SOFTWARE\Microsoft\Cryptography");
            var guid = key?.GetValue("MachineGuid") as string;
            return string.IsNullOrWhiteSpace(guid) ? null : Sha(guid);
        }
        catch { return null; }
    }

    private static string FromHardware()
    {
        var mac = NetworkInterface.GetAllNetworkInterfaces()
            .Where(n => n.OperationalStatus == OperationalStatus.Up &&
                        n.NetworkInterfaceType != NetworkInterfaceType.Loopback)
            .Select(n => n.GetPhysicalAddress().ToString())
            .FirstOrDefault(s => !string.IsNullOrEmpty(s)) ?? "NOMAC";
        return Sha(Environment.MachineName + "|" + mac);
    }

    private static string Sha(string input)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(input));
        return Convert.ToHexString(bytes);
    }
}

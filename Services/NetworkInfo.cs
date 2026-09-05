using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace UniversalPrintServer.Services;

/// <summary>
/// يعيد عناوين IPv4 المحلية الفعلية للحاسوب على الشبكة (Wi-Fi/Ethernet)،
/// مستبعداً عناوين Loopback وVirtual Adapters (VPN/Hyper-V/VMware) التي
/// تُربك المستخدم عادة عند محاولة معرفة أي IP يجب إدخاله في تطبيق الهاتف.
/// </summary>
public static class NetworkInfo
{
    public static List<(string AdapterName, string IpAddress)> GetLocalIPv4Addresses()
    {
        var result = new List<(string, string)>();

        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.OperationalStatus != OperationalStatus.Up)
                continue;

            // استبعاد المحولات الافتراضية الشائعة التي تسبب التباساً
            var desc = nic.Description.ToLowerInvariant();
            var name = nic.Name.ToLowerInvariant();
            if (desc.Contains("virtual") || desc.Contains("vmware") || desc.Contains("hyper-v") ||
                desc.Contains("loopback") || name.Contains("vethernet") ||
                nic.NetworkInterfaceType == NetworkInterfaceType.Loopback)
                continue;

            foreach (var addrInfo in nic.GetIPProperties().UnicastAddresses)
            {
                if (addrInfo.Address.AddressFamily == AddressFamily.InterNetwork)
                {
                    result.Add((nic.Name, addrInfo.Address.ToString()));
                }
            }
        }

        return result;
    }

    /// <summary>أفضل تخمين لعنوان IP "الرئيسي" — أول عنوان من محول Wi-Fi أو Ethernet فعّال.</summary>
    public static string? GetBestGuessIp()
    {
        var all = GetLocalIPv4Addresses();
        return all.FirstOrDefault().IpAddress;
    }
}

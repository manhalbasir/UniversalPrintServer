using System.Diagnostics;

namespace UniversalPrintServer.Services;

/// <summary>
/// يدير قواعد جدار حماية ويندوز اللازمة كي يصل الهاتف فعلياً للطابعة،
/// عبر أداة netsh القياسية المدمجة في كل نسخ ويندوز (لا تتطلب أي تبعية خارجية).
/// </summary>
public static class FirewallHelper
{
    /// <summary>يفعّل مجموعة قواعد "مشاركة الملفات والطابعات" المدمجة في ويندوز (ضرورية لـ SMB).</summary>
    public static void EnableFileAndPrinterSharing()
    {
        RunNetsh("advfirewall firewall set rule group=\"File and Printer Sharing\" new enable=yes");
    }

    /// <summary>يفتح منفذاً محدداً (TCP) في جدار الحماية باسم قاعدة مميّز، لخادم RAW/JetDirect الداخلي.</summary>
    public static (bool Success, string Message) OpenTcpPort(int port, string ruleName)
    {
        // نحذف أي قاعدة قديمة بنفس الاسم أولاً لتفادي التكرار عند كل تشغيل
        RunNetsh($"advfirewall firewall delete rule name=\"{ruleName}\"");

        var (success, output) = RunNetsh(
            $"advfirewall firewall add rule name=\"{ruleName}\" dir=in action=allow protocol=TCP localport={port}");

        return success
            ? (true, $"تم فتح المنفذ {port} في جدار الحماية")
            : (false, $"فشل فتح المنفذ {port}: {output}");
    }

    private static (bool Success, string Output) RunNetsh(string arguments)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "netsh",
                Arguments = arguments,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            using var process = Process.Start(psi);
            if (process == null) return (false, "تعذّر تشغيل netsh");

            string output = process.StandardOutput.ReadToEnd();
            string error = process.StandardError.ReadToEnd();
            process.WaitForExit();

            if (process.ExitCode != 0 && !string.IsNullOrWhiteSpace(error))
                return (false, error);

            return (true, output);
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }
}

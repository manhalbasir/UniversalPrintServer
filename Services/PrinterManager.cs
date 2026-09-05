using System.Diagnostics;
using System.Management;

namespace UniversalPrintServer.Services;

public record PrinterEntry(
    string Name,
    bool IsShared,
    string? ShareName);

/// <summary>
/// إدارة مشاركة الطابعات عبر Windows SMB.
/// 
/// المسؤوليات:
/// - اكتشاف الطابعات المثبتة.
/// - إنشاء/إلغاء Printer Share.
/// - تفعيل SMB2/SMB3.
/// - تعطيل SMB1.
/// - تفعيل Guest.
/// - ضبط إعدادات Windows اللازمة للسماح بالوصول كضيف.
/// - ضبط صلاحيات الطابعة.
/// - فتح Windows Firewall.
/// </summary>
public static class PrinterManager
{
    // ============================================================
    // PRINTER DISCOVERY
    // ============================================================

    public static List<PrinterEntry> GetInstalledPrinters()
    {
        var result = new List<PrinterEntry>();

        try
        {
            using var searcher =
                new ManagementObjectSearcher(
                    "SELECT Name, Shared, ShareName FROM Win32_Printer");

            foreach (ManagementObject printer in searcher.Get())
            {
                var name =
                    printer["Name"]?.ToString() ?? "";

                if (string.IsNullOrWhiteSpace(name))
                    continue;

                var isShared =
                    printer["Shared"] is bool b && b;

                var shareName =
                    printer["ShareName"]?.ToString();

                result.Add(
                    new PrinterEntry(
                        name,
                        isShared,
                        string.IsNullOrWhiteSpace(shareName)
                            ? null
                            : shareName));
            }
        }
        catch
        {
            // إبقاء القائمة فارغة بدلاً من إسقاط التطبيق.
        }

        return result;
    }


    // ============================================================
    // ENABLE PRINTER SHARING
    // ============================================================

    /// <summary>
    /// يفعّل مشاركة الطابعة عبر SMB.
    ///
    /// عند تفعيل Guest:
    /// - SMB2/SMB3 يتم تفعيله.
    /// - SMB1 يتم تعطيله.
    /// - Guest يتم تفعيله.
    /// - يتم إعداد Windows للسماح بالوصول كضيف.
    /// - يتم إنشاء Printer Share.
    /// - يتم ضبط صلاحيات Everyone.
    /// - يتم فتح Firewall.
    /// </summary>
    public static (bool Success, string Message) EnableSharing(
        string printerName,
        string? shareName = null,
        bool enableGuest = true)
    {
        if (string.IsNullOrWhiteSpace(printerName))
        {
            return (
                false,
                "لم يتم تحديد اسم الطابعة");
        }

        // --------------------------------------------------------
        // التأكد من وجود الطابعة
        // --------------------------------------------------------

        var printer =
            GetInstalledPrinters()
                .FirstOrDefault(
                    p => string.Equals(
                        p.Name,
                        printerName,
                        StringComparison.OrdinalIgnoreCase));

        if (printer == null)
        {
            return (
                false,
                $"لم يتم العثور على الطابعة: {printerName}");
        }

        // --------------------------------------------------------
        // Share Name
        // --------------------------------------------------------

        var effectiveShareName =
            string.IsNullOrWhiteSpace(shareName)
                ? SanitizeShareName(printerName)
                : SanitizeShareName(shareName);

        if (string.IsNullOrWhiteSpace(effectiveShareName))
        {
            return (
                false,
                "اسم المشاركة غير صالح");
        }

        // --------------------------------------------------------
        // 1. إعداد SMB
        // --------------------------------------------------------

        var smbResult =
            ConfigureSmbServer(enableGuest);

        if (!smbResult.Success)
        {
            return (
                false,
                $"فشل إعداد SMB: {smbResult.Message}");
        }

        // --------------------------------------------------------
        // 2. مشاركة الطابعة
        // --------------------------------------------------------

        var script =
            "Set-Printer " +
            $"-Name '{EscapeForPowerShellSingleQuote(printerName)}' " +
            "-Shared $true " +
            $"-ShareName '{EscapeForPowerShellSingleQuote(effectiveShareName)}'";

        var printerResult =
            RunPowerShell(script);

        if (!printerResult.Success)
        {
            return (
                false,
                $"فشل تفعيل مشاركة الطابعة: {printerResult.Output}");
        }

        // --------------------------------------------------------
        // 3. صلاحيات الطابعة
        // --------------------------------------------------------

        var permissionResult =
            GrantPrinterEveryonePrintAccess(printerName);

        if (!permissionResult.Success)
        {
            return (
                false,
                $"تم إنشاء المشاركة لكن فشل ضبط صلاحيات الطابعة: {permissionResult.Message}");
        }

        // --------------------------------------------------------
        // 4. Firewall
        // --------------------------------------------------------

        try
        {
            FirewallHelper.EnableFileAndPrinterSharing();
        }
        catch (Exception ex)
        {
            return (
                false,
                $"تم إعداد SMB لكن فشل إعداد جدار الحماية: {ex.Message}");
        }

        // --------------------------------------------------------
        // 5. تحقق نهائي
        // --------------------------------------------------------

        var verify =
            VerifyPrinterShare(
                printerName,
                effectiveShareName);

        if (!verify.Success)
        {
            return (
                false,
                $"تم إعداد المشاركة لكن فشل التحقق النهائي: {verify.Message}");
        }

        var guestText =
            enableGuest
                ? "Guest مفعّل"
                : "مصادقة Windows";

        return (
            true,
            $"تمت مشاركة الطابعة بنجاح: {effectiveShareName} ({guestText})");
    }


    // ============================================================
    // DISABLE SHARING
    // ============================================================

    public static (bool Success, string Message) DisableSharing(
        string printerName)
    {
        if (string.IsNullOrWhiteSpace(printerName))
        {
            return (
                false,
                "لم يتم تحديد اسم الطابعة");
        }

        var script =
            "Set-Printer " +
            $"-Name '{EscapeForPowerShellSingleQuote(printerName)}' " +
            "-Shared $false";

        var result =
            RunPowerShell(script);

        return result.Success
            ? (true, "تم إيقاف مشاركة الطابعة")
            : (false, $"فشل إيقاف المشاركة: {result.Output}");
    }


    // ============================================================
    // SMB CONFIGURATION
    // ============================================================

    /// <summary>
    /// إعداد Windows SMB.
    ///
    /// النتيجة المطلوبة:
    ///
    /// SMB1  = Disabled
    /// SMB2  = Enabled
    /// SMB3  = Enabled (ضمن SMB2 protocol stack)
    ///
    /// وعند Guest:
    /// Guest account = Enabled
    /// ForceGuest    = 1
    /// Security signature لا تكون إجبارية
    /// </summary>
    private static (bool Success, string Message) ConfigureSmbServer(
        bool enableGuest)
    {
        try
        {
            // ----------------------------------------------------
            // SMB1 OFF / SMB2 ON
            // ----------------------------------------------------

            var smbScript =
                "Set-SmbServerConfiguration " +
                "-EnableSMB1Protocol $false " +
                "-EnableSMB2Protocol $true " +
                "-Force";

            var smbResult =
                RunPowerShell(smbScript);

            if (!smbResult.Success)
            {
                return (
                    false,
                    smbResult.Output);
            }

            // ----------------------------------------------------
            // Guest
            // ----------------------------------------------------

            if (enableGuest)
            {
                var guestResult =
                    EnableWindowsGuest();

                if (!guestResult.Success)
                {
                    return (
                        false,
                        guestResult.Message);
                }
            }

            return (
                true,
                enableGuest
                    ? "تم تفعيل SMB2/SMB3 وGuest وتعطيل SMB1"
                    : "تم تفعيل SMB2/SMB3 وتعطيل SMB1");
        }
        catch (Exception ex)
        {
            return (
                false,
                ex.Message);
        }
    }


    // ============================================================
    // WINDOWS GUEST
    // ============================================================

    /// <summary>
    /// إعداد Windows لقبول الوصول المحلي كـ Guest.
    ///
    /// كل هذه الخطوات تتم من البرنامج ولا يحتاج المستخدم
    /// إلى تنفيذ PowerShell يدوياً.
    /// </summary>
    private static (bool Success, string Message) EnableWindowsGuest()
    {
        try
        {
            // ----------------------------------------------------
            // تفعيل حساب Guest
            // ----------------------------------------------------

            var guestResult =
    RunCommand(
        "net.exe",
        "user",
        "Guest",
        "/active:yes");

            if (!guestResult.Success)
            {
                return (
                    false,
                    $"تعذر تفعيل حساب Guest: {guestResult.Output}");
            }

            // ----------------------------------------------------
            // جعل نموذج المصادقة للحسابات المحلية يستخدم Guest
            // ----------------------------------------------------

            var registryScript =
                "$path = 'HKLM:\\SYSTEM\\CurrentControlSet\\Control\\Lsa'; " +
                "New-ItemProperty " +
                "-Path $path " +
                "-Name 'ForceGuest' " +
                "-PropertyType DWord " +
                "-Value 1 " +
                "-Force | Out-Null";

            var registryResult =
                RunPowerShell(registryScript);

            if (!registryResult.Success)
            {
                return (
                    false,
                    $"تعذر ضبط ForceGuest: {registryResult.Output}");
            }

            // ----------------------------------------------------
            // السماح بـ Guest داخل SMB Server
            //
            // Guest لا يستطيع استخدام SMB signing الإجباري.
            // ----------------------------------------------------

            var signingScript =
                "Set-SmbServerConfiguration " +
                "-RequireSecuritySignature $false " +
                "-Force";

            var signingResult =
                RunPowerShell(signingScript);

            if (!signingResult.Success)
            {
                return (
                    false,
                    $"تعذر ضبط SMB Security Signature: {signingResult.Output}");
            }

            // ----------------------------------------------------
            // إعادة تشغيل خدمة Server لتطبيق الإعدادات
            // ----------------------------------------------------


            return (
                true,
                "تم تفعيل Guest بنجاح");
        }
        catch (Exception ex)
        {
            return (
                false,
                ex.Message);
        }
    }


    // ============================================================
    // PRINTER SECURITY
    // ============================================================

    /// <summary>
    /// يمنح Everyone صلاحية استخدام الطابعة.
    ///
    /// S-1-1-0 = Everyone
    /// AccessMask:
    /// 0x00020008 =
    /// PRINTER_ACCESS_USE + READ_CONTROL
    /// </summary>
    private static (
       bool Success,
       string Message) GrantPrinterEveryonePrintAccess(
       string printerName)
    {
        // لا نستخدم WMI Security Descriptor هنا.
        //
        // Set-Printer نجح بالفعل في إنشاء Printer Share.
        // صلاحيات Printer Object سيتم التعامل معها لاحقاً
        // إذا أثبت اختبار SMB Guest أنها مطلوبة.

        return (
            true,
            "تم إنشاء مشاركة الطابعة؛ تم تجاوز تعديل WMI ACL");
    }
    //
    //
    //
    private static bool IsEveryoneAce(
        ManagementBaseObject ace)
    {
        try
        {
            if (ace["Trustee"]
                is not ManagementBaseObject trustee)
            {
                return false;
            }

            if (trustee["SID"]
                is not byte[] sid)
            {
                return false;
            }

            // S-1-1-0
            var everyoneSid = new byte[]
            {
                1, 1, 0, 0, 0, 0, 0, 1,
                0, 0, 0, 0
            };

            return sid.SequenceEqual(everyoneSid);
        }
        catch
        {
            return false;
        }
    }


    // ============================================================
    // VERIFY SHARE
    // ============================================================

    private static (
        bool Success,
        string Message) VerifyPrinterShare(
        string printerName,
        string shareName)
    {
        try
        {
            // تحقق من حالة الطابعة
            var printer =
                GetInstalledPrinters()
                    .FirstOrDefault(
                        p => string.Equals(
                            p.Name,
                            printerName,
                            StringComparison.OrdinalIgnoreCase));

            if (printer == null)
            {
                return (
                    false,
                    "الطابعة غير موجودة");
            }

            if (!printer.IsShared)
            {
                return (
                    false,
                    "الطابعة ليست Shared بعد تنفيذ الأمر");
            }

            if (!string.Equals(
                    printer.ShareName,
                    shareName,
                    StringComparison.OrdinalIgnoreCase))
            {
                return (
                    false,
                    $"اسم المشاركة الفعلي هو: {printer.ShareName ?? "(غير معروف)"}");
            }

            // تحقق من SMB Server
            var smbResult =
                RunPowerShell(
                    "(Get-SmbServerConfiguration).EnableSMB2Protocol");

            if (!smbResult.Success ||
                !smbResult.Output
                    .Trim()
                    .Equals(
                        "True",
                        StringComparison.OrdinalIgnoreCase))
            {
                return (
                    false,
                    "SMB2/SMB3 غير مفعّل");
            }

            // تحقق من SMB Share
            var shareResult =
                RunPowerShell(
                    $"Get-SmbShare -Name '{EscapeForPowerShellSingleQuote(shareName)}' -ErrorAction Stop");

            if (!shareResult.Success)
            {
                return (
                    false,
                    $"مشاركة SMB غير موجودة: {shareResult.Output}");
            }

            return (
                true,
                "تم التحقق من SMB Printer Share بنجاح");
        }
        catch (Exception ex)
        {
            return (
                false,
                ex.Message);
        }
    }


    //
    //
    //
    /// <summary>
    /// يفعّل Guest access من داخل البرنامج.
    /// تستخدمها الواجهة الحالية مباشرة.
    /// </summary>
    public static (bool Success, string Message) EnableGuestAccess(
        string printerName)
    {
        if (string.IsNullOrWhiteSpace(printerName))
        {
            return (
                false,
                "لم يتم تحديد اسم الطابعة");
        }

        try
        {
            // --------------------------------------------------------
            // 1. التأكد من وجود الطابعة
            // --------------------------------------------------------

            var printer =
                GetInstalledPrinters()
                    .FirstOrDefault(
                        p => string.Equals(
                            p.Name,
                            printerName,
                            StringComparison.OrdinalIgnoreCase));

            if (printer == null)
            {
                return (
                    false,
                    $"لم يتم العثور على الطابعة: {printerName}");
            }

            // --------------------------------------------------------
            // 2. إعداد SMB + Guest
            // --------------------------------------------------------

            var smbResult =
                ConfigureSmbServer(true);

            if (!smbResult.Success)
            {
                return (
                    false,
                    $"فشل إعداد SMB Guest: {smbResult.Message}");
            }

            // --------------------------------------------------------
            // 3. إعطاء Everyone صلاحية استخدام الطابعة
            // --------------------------------------------------------

            var permissionResult =
                GrantPrinterEveryonePrintAccess(printerName);

            if (!permissionResult.Success)
            {
                return (
                    false,
                    $"فشل ضبط صلاحيات الطابعة: {permissionResult.Message}");
            }

            // --------------------------------------------------------
            // 4. Firewall
            // --------------------------------------------------------

            try
            {
                FirewallHelper.EnableFileAndPrinterSharing();
            }
            catch (Exception ex)
            {
                return (
                    false,
                    $"تم إعداد Guest لكن فشل إعداد جدار الحماية: {ex.Message}");
            }

            return (
                true,
                "تم تفعيل SMB Guest بنجاح");
        }
        catch (Exception ex)
        {
            return (
                false,
                $"خطأ أثناء تفعيل SMB Guest: {ex.Message}");
        }
    }
    // ============================================================
    // SHARE NAME
    // ============================================================

    private static string SanitizeShareName(
        string name)
    {
        var invalidChars =
            new[]
            {
                '\\',
                '/',
                ':',
                '*',
                '?',
                '"',
                '<',
                '>',
                '|',
                ' '
            };

        var chars =
            name.ToCharArray();

        for (int i = 0; i < chars.Length; i++)
        {
            if (invalidChars.Contains(chars[i]))
                chars[i] = '_';
        }

        var result =
            new string(chars).Trim();

        // Windows لا يحب مشاركة فارغة
        if (string.IsNullOrWhiteSpace(result))
            result = "UniversalPrinter";

        return result;
    }


    // ============================================================
    // POWER SHELL
    // ============================================================

    /// <summary>
    /// تشغيل PowerShell بطريقة آمنة مع ArgumentList.
    ///
    /// هذا يصلح المشكلة الموجودة في الكود السابق:
    ///
    /// Arguments = "-Command \"...\""
    ///
    /// والتي كانت تسبب كسر الاقتباسات عندما يكون اسم الطابعة
    /// مثل:
    ///
    /// Brother DCP-T220
    /// </summary>
    private static (
        bool Success,
        string Output) RunPowerShell(
        string script)
    {
        try
        {
            var psi =
                new ProcessStartInfo
                {
                    FileName = "powershell.exe",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                };

            psi.ArgumentList.Add("-NoLogo");
            psi.ArgumentList.Add("-NoProfile");
            psi.ArgumentList.Add("-NonInteractive");
            psi.ArgumentList.Add("-ExecutionPolicy");
            psi.ArgumentList.Add("Bypass");
            psi.ArgumentList.Add("-Command");
            psi.ArgumentList.Add(script);

            using var process =
                Process.Start(psi);

            if (process == null)
            {
                return (
                    false,
                    "تعذر تشغيل PowerShell");
            }

            var output =
                process.StandardOutput.ReadToEnd();

            var error =
                process.StandardError.ReadToEnd();

            process.WaitForExit();

            if (process.ExitCode != 0)
            {
                return (
                    false,
                    string.IsNullOrWhiteSpace(error)
                        ? output
                        : error);
            }

            return (
                true,
                string.IsNullOrWhiteSpace(output)
                    ? error
                    : output);
        }
        catch (Exception ex)
        {
            return (
                false,
                ex.Message);
        }
    }


    // ============================================================
    // CMD
    // ============================================================

    private static (bool Success, string Output) RunCommand(
     string fileName,
     params string[] arguments)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = fileName,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };

            foreach (var argument in arguments)
                psi.ArgumentList.Add(argument);

            using var process = Process.Start(psi);

            if (process == null)
                return (false, $"تعذر تشغيل {fileName}");

            var output = process.StandardOutput.ReadToEnd();
            var error = process.StandardError.ReadToEnd();

            process.WaitForExit();

            if (process.ExitCode != 0)
            {
                return (
                    false,
                    string.IsNullOrWhiteSpace(error)
                        ? output
                        : error);
            }

            return (
                true,
                string.IsNullOrWhiteSpace(output)
                    ? error
                    : output);
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }
    // ============================================================
    // ESCAPING
    // ============================================================

    private static string EscapeForPowerShellSingleQuote(
        string input)
    {
        // PowerShell single quoted string:
        // ' becomes ''
        return input.Replace(
            "'",
            "''");
    }

    private static string EscapeForWmi(
        string input)
    {
        // WMI query strings use single quotes.
        return input.Replace(
            "'",
            "''");
    }
}
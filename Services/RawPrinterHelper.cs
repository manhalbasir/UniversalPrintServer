using System.Diagnostics;
using System.IO;
using System.Printing;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace UniversalPrintServer.Services;

public enum RawPrintDataType
{
    Raw,
    Jpeg,
    Png,
    Pdf
}

/// <summary>
/// أدوات الطباعة الخاصة بـ UniversalPrintServer.
///
/// RAW:
/// يرسل البيانات مباشرة إلى Windows Spooler كـ RAW.
///
/// الصور:
/// JPEG / PNG يتم تحويلها إلى WPF Visual ثم تمريرها
/// إلى Windows Print Queue، وبالتالي يستخدم تعريف الطابعة
/// المثبت فعلياً.
///
/// PDF:
/// يستخدم معالج PDF المسجل في Windows.
/// </summary>
public static class RawPrinterHelper
{
    // ============================================================
    // Win32 RAW PRINTING
    // ============================================================

    [StructLayout(
        LayoutKind.Sequential,
        CharSet = CharSet.Ansi)]
    private struct DOCINFOA
    {
        [MarshalAs(UnmanagedType.LPStr)]
        public string pDocName;

        [MarshalAs(UnmanagedType.LPStr)]
        public string? pOutputFile;

        [MarshalAs(UnmanagedType.LPStr)]
        public string pDataType;
    }

    [DllImport(
        "winspool.drv",
        CharSet = CharSet.Ansi,
        SetLastError = true,
        ExactSpelling = true)]
    private static extern bool OpenPrinterA(
        string pPrinterName,
        out IntPtr phPrinter,
        IntPtr pDefault);

    [DllImport(
        "winspool.drv",
        SetLastError = true)]
    private static extern bool ClosePrinter(
        IntPtr hPrinter);

    [DllImport(
        "winspool.drv",
        EntryPoint = "StartDocPrinterA",
        CharSet = CharSet.Ansi,
        SetLastError = true)]
    private static extern int StartDocPrinter(
        IntPtr hPrinter,
        int level,
        [In] ref DOCINFOA pDocInfo);

    [DllImport(
        "winspool.drv",
        SetLastError = true)]
    private static extern bool EndDocPrinter(
        IntPtr hPrinter);

    [DllImport(
        "winspool.drv",
        SetLastError = true)]
    private static extern bool StartPagePrinter(
        IntPtr hPrinter);

    [DllImport(
        "winspool.drv",
        SetLastError = true)]
    private static extern bool EndPagePrinter(
        IntPtr hPrinter);

    [DllImport(
        "winspool.drv",
        SetLastError = true)]
    private static extern bool WritePrinter(
        IntPtr hPrinter,
        byte[] pBytes,
        int dwCount,
        out int dwWritten);

    /// <summary>
    /// يرسل بيانات جاهزة للطابعة مباشرة بصيغة RAW.
    ///
    /// يستخدم للبيانات مثل:
    /// PCL / PostScript / ESC/POS / Printer-specific RAW
    /// </summary>
    public static bool SendBytesToPrinter(
        string printerName,
        byte[] bytes,
        string jobName = "UniversalPrint Raw Job")
    {
        if (bytes == null || bytes.Length == 0)
            return false;

        if (!OpenPrinterA(
                printerName,
                out IntPtr hPrinter,
                IntPtr.Zero))
        {
            return false;
        }

        try
        {
            var docInfo = new DOCINFOA
            {
                pDocName = jobName,
                pOutputFile = null,
                pDataType = "RAW"
            };

            if (StartDocPrinter(
                    hPrinter,
                    1,
                    ref docInfo) == 0)
            {
                return false;
            }

            try
            {
                if (!StartPagePrinter(hPrinter))
                    return false;

                try
                {
                    bool result =
                        WritePrinter(
                            hPrinter,
                            bytes,
                            bytes.Length,
                            out int written);

                    return result &&
                           written == bytes.Length;
                }
                finally
                {
                    EndPagePrinter(hPrinter);
                }
            }
            finally
            {
                EndDocPrinter(hPrinter);
            }
        }
        finally
        {
            ClosePrinter(hPrinter);
        }
    }

    // ============================================================
    // DATA TYPE DETECTION
    // ============================================================

    /// <summary>
    /// يتعرف على نوع البيانات من Magic Bytes.
    /// </summary>
    public static RawPrintDataType DetectDataType(
        byte[] bytes)
    {
        if (bytes == null || bytes.Length < 4)
            return RawPrintDataType.Raw;

        // --------------------------------------------------------
        // JPEG
        // FF D8 FF
        // --------------------------------------------------------

        if (bytes.Length >= 3 &&
            bytes[0] == 0xFF &&
            bytes[1] == 0xD8 &&
            bytes[2] == 0xFF)
        {
            return RawPrintDataType.Jpeg;
        }

        // --------------------------------------------------------
        // PNG
        // 89 50 4E 47 0D 0A 1A 0A
        // --------------------------------------------------------

        if (bytes.Length >= 8 &&
            bytes[0] == 0x89 &&
            bytes[1] == 0x50 &&
            bytes[2] == 0x4E &&
            bytes[3] == 0x47 &&
            bytes[4] == 0x0D &&
            bytes[5] == 0x0A &&
            bytes[6] == 0x1A &&
            bytes[7] == 0x0A)
        {
            return RawPrintDataType.Png;
        }

        // --------------------------------------------------------
        // PDF
        // %PDF
        // 25 50 44 46
        // --------------------------------------------------------

        if (bytes.Length >= 4 &&
            bytes[0] == 0x25 &&
            bytes[1] == 0x50 &&
            bytes[2] == 0x44 &&
            bytes[3] == 0x46)
        {
            return RawPrintDataType.Pdf;
        }

        return RawPrintDataType.Raw;
    }

    // ============================================================
    // WINDOWS DRIVER IMAGE PRINTING
    // ============================================================

    /// <summary>
    /// يطبع JPEG/PNG باستخدام Windows Print Queue.
    ///
    /// هذه العملية لا تستخدم RAW.
    /// بل تحول الصورة إلى WPF Visual ثم ترسلها
    /// إلى تعريف الطابعة المثبت على Windows.
    /// </summary>
    public static (
        bool Success,
        string Message) PrintImageViaWindowsDriver(
        string printerName,
        byte[] imageBytes,
        string jobName = "UniversalPrint Image")
    {
        if (imageBytes == null ||
            imageBytes.Length == 0)
        {
            return (
                false,
                "بيانات الصورة فارغة");
        }

        try
        {
            // WPF يجب أن يعمل على Dispatcher الخاص بالتطبيق.
            var dispatcher =
                Application.Current?.Dispatcher;

            if (dispatcher == null)
            {
                return (
                    false,
                    "لم يتم العثور على WPF Dispatcher");
            }

            return dispatcher.Invoke(() =>
                PrintImageOnDispatcher(
                    printerName,
                    imageBytes,
                    jobName));
        }
        catch (Exception ex)
        {
            return (
                false,
                $"خطأ أثناء طباعة الصورة: {ex.Message}");
        }
    }

    private static (
        bool Success,
        string Message) PrintImageOnDispatcher(
        string printerName,
        byte[] imageBytes,
        string jobName)
    {
        BitmapImage? bitmap = null;

        try
        {
            // ----------------------------------------------------
            // Decode image
            // ----------------------------------------------------

            using var stream =
                new MemoryStream(imageBytes);

            bitmap = new BitmapImage();

            bitmap.BeginInit();

            bitmap.CacheOption =
                BitmapCacheOption.OnLoad;

            bitmap.StreamSource = stream;

            bitmap.EndInit();

            bitmap.Freeze();

            if (bitmap.PixelWidth <= 0 ||
                bitmap.PixelHeight <= 0)
            {
                return (
                    false,
                    "تعذر قراءة أبعاد الصورة");
            }

            // ----------------------------------------------------
            // الحصول على Print Queue
            // ----------------------------------------------------

            using var printServer =
                new LocalPrintServer();

            var printQueue =
                printServer.GetPrintQueue(
                    printerName);

            if (printQueue == null)
            {
                return (
                    false,
                    $"لم يتم العثور على طابور الطباعة: {printerName}");
            }

            printQueue.Refresh();

            // ----------------------------------------------------
            // Print capabilities
            // ----------------------------------------------------

            var capabilities =
                printQueue.GetPrintCapabilities();

            double pageWidth = 793.7;
            double pageHeight = 1122.5;

            if (capabilities.PageImageableArea != null)
            {
                var area = capabilities.PageImageableArea;

                pageWidth = area.ExtentWidth;
                pageHeight = area.ExtentHeight;
            }
            // ----------------------------------------------------
            // حساب أبعاد الصورة مع الحفاظ على النسبة
            // ----------------------------------------------------

            double imageWidth =
                bitmap.PixelWidth;

            double imageHeight =
                bitmap.PixelHeight;

            double scaleX =
                pageWidth / imageWidth;

            double scaleY =
                pageHeight / imageHeight;

            double scale =
                Math.Min(scaleX, scaleY);

            // هامش بسيط
            scale *= 0.95;

            double drawWidth =
                imageWidth * scale;

            double drawHeight =
                imageHeight * scale;

            double x =
                (pageWidth - drawWidth) / 2.0;

            double y =
                (pageHeight - drawHeight) / 2.0;

            // ----------------------------------------------------
            // إنشاء Visual
            // ----------------------------------------------------

            var visual =
                new DrawingVisual();

            using (DrawingContext dc =
                   visual.RenderOpen())
            {
                dc.DrawImage(
                    bitmap,
                    new Rect(
                        x,
                        y,
                        drawWidth,
                        drawHeight));
            }

            // ----------------------------------------------------
            // إرسال إلى Windows Print Driver
            // ----------------------------------------------------

            var printDialog =
                new System.Windows.Controls.PrintDialog
                {
                    PrintQueue = printQueue
                };

            printDialog.PrintVisual(
                visual,
                jobName);

            return (
                true,
                $"تمت طباعة الصورة بنجاح باستخدام تعريف Windows للطابعة \"{printerName}\"");
        }
        catch (PrintQueueException ex)
        {
            return (
                false,
                $"خطأ في طابور الطباعة: {ex.Message}");
        }
        catch (Exception ex)
        {
            return (
                false,
                $"خطأ في طباعة الصورة: {ex.Message}");
        }
    }

    // ============================================================
    // DEFAULT PRINTER
    // ============================================================

    [DllImport(
        "winspool.drv",
        CharSet = CharSet.Auto,
        SetLastError = true)]
    private static extern bool SetDefaultPrinter(
        string? Name);

    [DllImport(
        "winspool.drv",
        CharSet = CharSet.Auto,
        SetLastError = true)]
    private static extern bool GetDefaultPrinter(
        StringBuilder pszBuffer,
        ref int pcchBuffer);

    private static string? GetCurrentDefaultPrinter()
    {
        int size = 260;

        var sb =
            new StringBuilder(size);

        return GetDefaultPrinter(
            sb,
            ref size)
            ? sb.ToString()
            : null;
    }

    // ============================================================
    // PDF PRINTING
    // ============================================================

    /// <summary>
    /// يطبع PDF باستخدام معالج PDF المسجل في Windows.
    /// </summary>
    public static (
        bool Success,
        string Message) PrintPdfViaShellHandler(
        string printerName,
        byte[] pdfBytes)
    {
        if (pdfBytes == null ||
            pdfBytes.Length == 0)
        {
            return (
                false,
                "بيانات PDF فارغة");
        }

        var tempFile =
            Path.Combine(
                Path.GetTempPath(),
                $"universalprint_{Guid.NewGuid():N}.pdf");

        string? originalDefault = null;

        try
        {
            File.WriteAllBytes(
                tempFile,
                pdfBytes);

            originalDefault =
                GetCurrentDefaultPrinter();

            if (!SetDefaultPrinter(printerName))
            {
                return (
                    false,
                    $"تعذّر تعيين \"{printerName}\" كطابعة افتراضية مؤقتاً");
            }

            var psi =
                new ProcessStartInfo
                {
                    FileName = tempFile,
                    Verb = "print",
                    UseShellExecute = true,
                    CreateNoWindow = true,
                    WindowStyle =
                        ProcessWindowStyle.Hidden
                };

            using var process =
                Process.Start(psi);

            if (process == null)
            {
                return (
                    false,
                    "تعذّر تشغيل معالج PDF المسجل في Windows");
            }

            process.WaitForExit(20000);

            // إعطاء Spooler وقتًا لتسجيل المهمة.
            Thread.Sleep(3000);

            return (
                true,
                $"تم إرسال PDF إلى \"{printerName}\"");
        }
        catch (Exception ex)
        {
            return (
                false,
                $"فشل تنفيذ طباعة PDF: {ex.Message}");
        }
        finally
        {
            if (originalDefault != null)
            {
                try
                {
                    SetDefaultPrinter(
                        originalDefault);
                }
                catch
                {
                }
            }

            try
            {
                File.Delete(tempFile);
            }
            catch
            {
            }
        }
    }
}
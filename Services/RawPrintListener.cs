using System.Net;
using System.Net.Sockets;
using System.IO;
namespace UniversalPrintServer.Services;

/// <summary>
/// خادم RAW/JetDirect على TCP 9100.
/// يستقبل البيانات من العميل ثم يحدد نوعها تلقائياً:
/// JPEG / PNG / PDF / RAW.
///
/// الصور يتم إرسالها عبر Windows Print Driver حتى تستطيع
/// الطابعات المنزلية مثل Brother DCP-T220 طباعتها.
/// أما البيانات RAW الحقيقية فتبقى في مسار RAW المباشر.
/// </summary>
public sealed class RawPrintListener : IDisposable
{
    private TcpListener? _listener;
    private CancellationTokenSource? _cts;

    public bool IsRunning { get; private set; }
    public int Port { get; private set; }

    public event Action<string>? JobReceived;
    public event Action<string>? ErrorOccurred;

    private readonly Func<string?> _targetPrinterNameProvider;
    private readonly Func<bool> _gdiModeProvider;

    public RawPrintListener(
        Func<string?> targetPrinterNameProvider,
        Func<bool>? gdiModeProvider = null)
    {
        _targetPrinterNameProvider = targetPrinterNameProvider;
        _gdiModeProvider = gdiModeProvider ?? (() => false);
    }

    public void Start(int port = 9100)
    {
        if (IsRunning)
            return;

        _cts = new CancellationTokenSource();

        try
        {
            _listener = new TcpListener(IPAddress.Any, port);
            _listener.Start();

            Port = port;
            IsRunning = true;

            _ = AcceptLoopAsync(_cts.Token);
        }
        catch
        {
            IsRunning = false;
            _listener = null;
            throw;
        }
    }

    public void Stop()
    {
        IsRunning = false;

        try
        {
            _cts?.Cancel();
        }
        catch
        {
        }

        try
        {
            _listener?.Stop();
        }
        catch
        {
        }

        _listener = null;

        try
        {
            _cts?.Dispose();
        }
        catch
        {
        }

        _cts = null;
    }

    private async Task AcceptLoopAsync(CancellationToken token)
    {
        try
        {
            while (!token.IsCancellationRequested && _listener != null)
            {
                var client = await _listener.AcceptTcpClientAsync(token);

                // كل عميل تتم معالجته بشكل مستقل.
                _ = HandleClientAsync(client, token);
            }
        }
        catch (OperationCanceledException)
        {
            // إيقاف طبيعي.
        }
        catch (ObjectDisposedException)
        {
            // إيقاف طبيعي.
        }
        catch (Exception ex)
        {
            ErrorOccurred?.Invoke(
                $"خطأ في خادم RAW: {ex.Message}");
        }
    }

    private async Task HandleClientAsync(
        TcpClient client,
        CancellationToken token)
    {
        using (client)
        using (var stream = client.GetStream())
        using (var buffer = new MemoryStream())
        {
            try
            {
                // نقرأ كامل المهمة حتى يغلق العميل الاتصال.
                await stream.CopyToAsync(buffer, token);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                ErrorOccurred?.Invoke(
                    $"تعذّرت قراءة بيانات الطباعة: {ex.Message}");

                return;
            }

            var bytes = buffer.ToArray();

            if (bytes.Length == 0)
                return;

            var printerName = _targetPrinterNameProvider();

            if (string.IsNullOrWhiteSpace(printerName))
            {
                ErrorOccurred?.Invoke(
                    "لم يتم اختيار طابعة هدف لخادم RAW");

                return;
            }

            try
            {
                var dataType =
                    RawPrinterHelper.DetectDataType(bytes);

                switch (dataType)
                {
                    case RawPrintDataType.Jpeg:

                        JobReceived?.Invoke(
                            $"🖨️ RAW: تم استلام {bytes.Length:N0} بايت - JPEG");

                        var jpegResult =
                            RawPrinterHelper.PrintImageViaWindowsDriver(
                                printerName,
                                bytes,
                                "UniversalPrint JPEG");

                        if (jpegResult.Success)
                        {
                            JobReceived?.Invoke(
                                $"✅ تمت طباعة صورة JPEG عبر تعريف Windows إلى \"{printerName}\"");
                        }
                        else
                        {
                            ErrorOccurred?.Invoke(
                                $"❌ فشل طباعة JPEG: {jpegResult.Message}");
                        }

                        break;

                    case RawPrintDataType.Png:

                        JobReceived?.Invoke(
                            $"🖨️ RAW: تم استلام {bytes.Length:N0} بايت - PNG");

                        var pngResult =
                            RawPrinterHelper.PrintImageViaWindowsDriver(
                                printerName,
                                bytes,
                                "UniversalPrint PNG");

                        if (pngResult.Success)
                        {
                            JobReceived?.Invoke(
                                $"✅ تمت طباعة صورة PNG عبر تعريف Windows إلى \"{printerName}\"");
                        }
                        else
                        {
                            ErrorOccurred?.Invoke(
                                $"❌ فشل طباعة PNG: {pngResult.Message}");
                        }

                        break;

                    case RawPrintDataType.Pdf:

                        JobReceived?.Invoke(
                            $"🖨️ RAW: تم استلام {bytes.Length:N0} بايت - PDF");

                        var pdfResult =
                            RawPrinterHelper.PrintPdfViaShellHandler(
                                printerName,
                                bytes);

                        if (pdfResult.Success)
                        {
                            JobReceived?.Invoke(
                                $"✅ تم إرسال PDF إلى \"{printerName}\" عبر تعريف Windows");
                        }
                        else
                        {
                            ErrorOccurred?.Invoke(
                                $"❌ فشل طباعة PDF: {pdfResult.Message}");
                        }

                        break;

                    case RawPrintDataType.Raw:
                    default:

                        JobReceived?.Invoke(
                            $"🖨️ RAW: بيانات غير معروفة/Printer-Ready ({bytes.Length:N0} بايت)");

                        bool rawSuccess =
                            RawPrinterHelper.SendBytesToPrinter(
                                printerName,
                                bytes,
                                "UniversalPrint Raw Job");

                        if (rawSuccess)
                        {
                            JobReceived?.Invoke(
                                $"✅ تم إرسال RAW مباشرة إلى \"{printerName}\"");
                        }
                        else
                        {
                            ErrorOccurred?.Invoke(
                                $"❌ فشل إرسال RAW ({bytes.Length:N0} بايت) إلى \"{printerName}\"");
                        }

                        break;
                }
            }
            catch (Exception ex)
            {
                ErrorOccurred?.Invoke(
                    $"❌ خطأ أثناء معالجة مهمة RAW: {ex.Message}");
            }
        }
    }

    public void Dispose()
    {
        Stop();
    }
}
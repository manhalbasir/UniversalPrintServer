using System.IO;
using System.Net;

namespace UniversalPrintServer.Services;

/// <summary>
/// خادم IPP خفيف جداً — مبني خصيصاً ليطابق عميل IppClient.kt في تطبيق
/// UniversalPrinter للأندرويد (وليس تطبيقاً كاملاً لمواصفة IPP/CUPS).
/// بما أننا نتحكم في الطرفين (العميل على الهاتف والخادم هنا)، يمكننا تبسيط
/// تحليل بروتوكول IPP الثنائي دون فقدان التوافق الفعلي بينهما.
///
/// يدعم عمليتين فقط (وهما كل ما يستخدمه التطبيق):
/// - Get-Printer-Attributes (0x000B): تُستخدم فقط لأغراض "التحقق من الاتصال"
/// - Print-Job (0x0002): يستخرج بيانات المستند بعد وسم نهاية الخصائص (0x03)
///   ويرسلها مباشرة لطابور طباعة ويندوز.
/// </summary>
public sealed class IppServer : IDisposable
{
    private HttpListener? _listener;
    private CancellationTokenSource? _cts;

    public bool IsRunning { get; private set; }
    public event Action<string>? JobReceived;
    public event Action<string>? ErrorOccurred;

    private readonly Func<string?> _targetPrinterNameProvider;

    public IppServer(Func<string?> targetPrinterNameProvider)
    {
        _targetPrinterNameProvider = targetPrinterNameProvider;
    }

    public void Start(int port = 631)
    {
        if (IsRunning) return;

        _cts = new CancellationTokenSource();
        _listener = new HttpListener();
        // البادئة العامة "+" تعني الاستماع على كل عناوين IP المحلية؛ يتطلب
        // صلاحيات المسؤول (متوفرة أصلاً لأن كل التطبيق يعمل مرفوعاً كمسؤول).
        _listener.Prefixes.Add($"http://+:{port}/");
        _listener.Start();
        IsRunning = true;

        _ = AcceptLoopAsync(_cts.Token);
    }

    public void Stop()
    {
        IsRunning = false;
        _cts?.Cancel();
        try { _listener?.Stop(); } catch { /* تجاهل أخطاء الإغلاق العادية */ }
    }

    private async Task AcceptLoopAsync(CancellationToken token)
    {
        try
        {
            while (!token.IsCancellationRequested && _listener != null)
            {
                var context = await _listener.GetContextAsync();
                _ = HandleRequestAsync(context, token);
            }
        }
        catch (Exception ex) when (!token.IsCancellationRequested)
        {
            ErrorOccurred?.Invoke($"خطأ في خادم IPP: {ex.Message}");
        }
        catch { /* إيقاف طبيعي عند إلغاء الرمز المميز */ }
    }

    private async Task HandleRequestAsync(HttpListenerContext context, CancellationToken token)
    {
        try
        {
            byte[] body;
            using (var ms = new MemoryStream())
            {
                await context.Request.InputStream.CopyToAsync(ms, token);
                body = ms.ToArray();
            }

            var parsed = ParseIppRequest(body);

            if (parsed.OperationId == 0x0002 && parsed.DocumentBytes.Length > 0) // Print-Job
            {
                var printerName = _targetPrinterNameProvider();
                if (string.IsNullOrWhiteSpace(printerName))
                {
                    ErrorOccurred?.Invoke("IPP: لم يتم اختيار طابعة هدف");
                }
                else
                {
                    var ok = RawPrinterHelper.SendBytesToPrinter(printerName, parsed.DocumentBytes);
                    if (ok)
                        JobReceived?.Invoke($"IPP: تم استلام وإرسال {parsed.DocumentBytes.Length:N0} بايت إلى \"{printerName}\"");
                    else
                        ErrorOccurred?.Invoke($"IPP: فشل إرسال المهمة إلى \"{printerName}\"");
                }
            }
            else if (parsed.OperationId == 0x000B) // Get-Printer-Attributes (تحقق الاتصال)
            {
                JobReceived?.Invoke("IPP: استعلام تحقق من الاتصال (بدون طباعة)");
            }

            var responseBytes = BuildMinimalIppResponse(parsed.RequestId);
            context.Response.StatusCode = 200;
            context.Response.ContentType = "application/ipp";
            context.Response.ContentLength64 = responseBytes.Length;
            await context.Response.OutputStream.WriteAsync(responseBytes, token);
            context.Response.OutputStream.Close();
        }
        catch (Exception ex)
        {
            ErrorOccurred?.Invoke($"IPP: خطأ في معالجة الطلب: {ex.Message}");
            try { context.Response.StatusCode = 500; context.Response.Close(); } catch { }
        }
    }

    private record ParsedIppRequest(int OperationId, int RequestId, byte[] DocumentBytes);

    /// <summary>
    /// يحلّل رأس طلب IPP الثنائي بنفس منطق الكتابة المستخدَم في IppClient.kt
    /// (نفس الجهة كتبت الطرفين، لذا التحليل المُبسَّط هذا آمن وكافٍ).
    /// </summary>
    private static ParsedIppRequest ParseIppRequest(byte[] data)
    {
        if (data.Length < 8) return new ParsedIppRequest(0, 0, Array.Empty<byte>());

        int operationId = (data[2] << 8) | data[3];
        int requestId = (data[4] << 24) | (data[5] << 16) | (data[6] << 8) | data[7];

        int pos = 8;
        while (pos < data.Length)
        {
            byte tag = data[pos];
            pos++;

            if (tag == 0x03) // end-of-attributes-tag
                break;

            if (tag <= 0x0F)
                continue; // وسم فاصل بلا اسم/قيمة (مثل operation-attributes-tag = 0x01)

            // وسم قيمة: name-length(2) + name + value-length(2) + value
            if (pos + 2 > data.Length) break;
            int nameLen = (data[pos] << 8) | data[pos + 1];
            pos += 2 + nameLen;
            if (pos + 2 > data.Length) break;
            int valueLen = (data[pos] << 8) | data[pos + 1];
            pos += 2 + valueLen;
        }

        var documentBytes = pos < data.Length ? data[pos..] : Array.Empty<byte>();
        return new ParsedIppRequest(operationId, requestId, documentBytes);
    }

    /// <summary>رد IPP ناجح بالحد الأدنى (successful-ok) — كافٍ تماماً لعميلنا الذي يتحقق فقط من رمز HTTP.</summary>
    private static byte[] BuildMinimalIppResponse(int requestId)
    {
        using var ms = new MemoryStream();
        ms.WriteByte(0x02); ms.WriteByte(0x00);       // IPP version 2.0
        ms.WriteByte(0x00); ms.WriteByte(0x00);       // status-code: successful-ok
        ms.WriteByte((byte)(requestId >> 24)); ms.WriteByte((byte)(requestId >> 16));
        ms.WriteByte((byte)(requestId >> 8)); ms.WriteByte((byte)requestId);

        ms.WriteByte(0x01); // operation-attributes-tag
        WriteAttribute(ms, 0x47, "attributes-charset", "utf-8");
        WriteAttribute(ms, 0x48, "attributes-natural-language", "en");
        ms.WriteByte(0x03); // end-of-attributes-tag

        return ms.ToArray();
    }

    private static void WriteAttribute(Stream s, byte tag, string name, string value)
    {
        s.WriteByte(tag);
        WriteShortString(s, name);
        WriteShortString(s, value);
    }

    private static void WriteShortString(Stream s, string value)
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes(value);
        s.WriteByte((byte)(bytes.Length >> 8));
        s.WriteByte((byte)bytes.Length);
        s.Write(bytes, 0, bytes.Length);
    }

    public void Dispose() => Stop();
}

using System.Net;
using System.Net.Sockets;
using System.Text;

namespace UniversalPrintServer.Services;

/// <summary>
/// خادم LPR/LPD مبسّط (RFC 1179) — يدعم فقط تسلسل "استقبال مهمة طباعة"
/// (أمر 0x02) واستقبال ملف تحكم واحد وملف بيانات واحد، وهو بالضبط ما
/// يرسله عميل LprPrintClient في تطبيق الأندرويد. لا يدعم قوائم الانتظار
/// المتعددة أو حذف المهام، لأن هذا خادم مطابقة (Interop) وليس LPD كاملاً.
/// </summary>
public sealed class LpdServer : IDisposable
{
    private TcpListener? _listener;
    private CancellationTokenSource? _cts;

    public bool IsRunning { get; private set; }
    public event Action<string>? JobReceived;
    public event Action<string>? ErrorOccurred;

    private readonly Func<string?> _targetPrinterNameProvider;

    public LpdServer(Func<string?> targetPrinterNameProvider)
    {
        _targetPrinterNameProvider = targetPrinterNameProvider;
    }

    public void Start(int port = 515)
    {
        if (IsRunning) return;
        _cts = new CancellationTokenSource();
        _listener = new TcpListener(IPAddress.Any, port);
        _listener.Start();
        IsRunning = true;
        _ = AcceptLoopAsync(_cts.Token);
    }

    public void Stop()
    {
        IsRunning = false;
        _cts?.Cancel();
        try { _listener?.Stop(); } catch { }
    }

    private async Task AcceptLoopAsync(CancellationToken token)
    {
        try
        {
            while (!token.IsCancellationRequested && _listener != null)
            {
                var client = await _listener.AcceptTcpClientAsync(token);
                _ = HandleClientAsync(client, token);
            }
        }
        catch (OperationCanceledException) { }
        catch (ObjectDisposedException) { }
        catch (Exception ex)
        {
            ErrorOccurred?.Invoke($"خطأ في خادم LPR: {ex.Message}");
        }
    }

    private async Task HandleClientAsync(TcpClient client, CancellationToken token)
    {
        using (client)
        using (var stream = client.GetStream())
        {
            try
            {
                int firstCmd = stream.ReadByte();
                if (firstCmd != 0x02) return; // ندعم فقط "Receive a printer job"

                var queueName = ReadLine(stream);
                AckByte(stream);

                byte[]? dataFileBytes = null;

                while (true)
                {
                    int sub = stream.ReadByte();
                    if (sub == -1 || sub == 0x01) break; // انتهى الاتصال أو أمر إلغاء

                    var header = ReadLine(stream); // "<byteCount> <filename>"
                    var spaceIdx = header.IndexOf(' ');
                    if (spaceIdx < 0 || !long.TryParse(header[..spaceIdx], out long byteCount))
                    {
                        NackByte(stream);
                        continue;
                    }
                    AckByte(stream);

                    var content = await ReadExactAsync(stream, byteCount, token);
                    stream.ReadByte(); // بايت الإنهاء (يجب أن يكون 0x00)
                    AckByte(stream);

                    if (sub == 0x03) // ملف البيانات — هذا ما نطبعه فعلياً
                    {
                        dataFileBytes = content;
                        break; // تبسيط معقول: مهمة واحدة لكل اتصال
                    }
                    // sub == 0x02 (ملف التحكم) — نستقبله وفق البروتوكول، ونتجاهل محتواه
                }

                if (dataFileBytes is { Length: > 0 })
                {
                    var printerName = _targetPrinterNameProvider();
                    if (string.IsNullOrWhiteSpace(printerName))
                    {
                        ErrorOccurred?.Invoke("LPR: لم يتم اختيار طابعة هدف");
                        return;
                    }
                    var ok = RawPrinterHelper.SendBytesToPrinter(printerName, dataFileBytes);
                    if (ok)
                        JobReceived?.Invoke($"LPR: تم إرسال {dataFileBytes.Length:N0} بايت إلى \"{printerName}\" (Queue: {queueName})");
                    else
                        ErrorOccurred?.Invoke($"LPR: فشل إرسال المهمة إلى \"{printerName}\"");
                }
            }
            catch (Exception ex)
            {
                ErrorOccurred?.Invoke($"LPR: خطأ أثناء المعالجة: {ex.Message}");
            }
        }
    }

    private static void AckByte(NetworkStream s) => s.WriteByte(0x00);
    private static void NackByte(NetworkStream s) => s.WriteByte(0x01);

    private static string ReadLine(NetworkStream stream)
    {
        var buffer = new List<byte>();
        int b;
        while ((b = stream.ReadByte()) != -1 && b != (byte)'\n')
            buffer.Add((byte)b);
        return Encoding.ASCII.GetString(buffer.ToArray()).TrimEnd('\r');
    }

    private static async Task<byte[]> ReadExactAsync(NetworkStream stream, long count, CancellationToken token)
    {
        var buffer = new byte[count];
        int offset = 0;
        while (offset < count)
        {
            int read = await stream.ReadAsync(buffer.AsMemory(offset, (int)(count - offset)), token);
            if (read == 0) break;
            offset += read;
        }
        return buffer;
    }

    public void Dispose() => Stop();
}

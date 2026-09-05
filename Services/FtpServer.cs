using System.Net;
using System.Net.Sockets;
using System.Text;
using System.IO;
namespace UniversalPrintServer.Services;

/// <summary>
/// خادم FTP مبسّط جداً — يقبل أي اسم مستخدم/كلمة مرور (لا يوجد أي تحقق
/// فعلي؛ الهدف هو التوافق مع FtpPrintClient في تطبيق الأندرويد فقط)،
/// ويدعم فقط ما يلزم لاستقبال ملف: USER/PASS/SYST/TYPE/PWD/CWD/PASV/STOR/QUIT.
/// كل ملف يُرفع عبر STOR يُطبع فوراً على الطابعة المُختارة.
/// </summary>
public sealed class FtpServer : IDisposable
{
    private TcpListener? _controlListener;
    private CancellationTokenSource? _cts;

    public bool IsRunning { get; private set; }
    public event Action<string>? JobReceived;
    public event Action<string>? ErrorOccurred;

    private readonly Func<string?> _targetPrinterNameProvider;
    private readonly Func<IPAddress?> _localIpProvider;

    public FtpServer(Func<string?> targetPrinterNameProvider, Func<IPAddress?> localIpProvider)
    {
        _targetPrinterNameProvider = targetPrinterNameProvider;
        _localIpProvider = localIpProvider;
    }

    public void Start(int port = 21)
    {
        if (IsRunning) return;
        _cts = new CancellationTokenSource();
        _controlListener = new TcpListener(IPAddress.Any, port);
        _controlListener.Start();
        IsRunning = true;
        _ = AcceptLoopAsync(_cts.Token);
    }

    public void Stop()
    {
        IsRunning = false;
        _cts?.Cancel();
        try { _controlListener?.Stop(); } catch { }
    }

    private async Task AcceptLoopAsync(CancellationToken token)
    {
        try
        {
            while (!token.IsCancellationRequested && _controlListener != null)
            {
                var client = await _controlListener.AcceptTcpClientAsync(token);
                _ = HandleControlConnectionAsync(client, token);
            }
        }
        catch (OperationCanceledException) { }
        catch (ObjectDisposedException) { }
        catch (Exception ex)
        {
            ErrorOccurred?.Invoke($"خطأ في خادم FTP: {ex.Message}");
        }
    }

    private async Task HandleControlConnectionAsync(TcpClient control, CancellationToken token)
    {
        using (control)
        using (var controlStream = control.GetStream())
        {
            TcpListener? dataListener = null;
            try
            {
                await WriteLineAsync(controlStream, "220 UniversalPrint FTP ready", token);

                while (!token.IsCancellationRequested)
                {
                    var line = await ReadLineAsync(controlStream, token);
                    if (line == null) break;

                    var spaceIdx = line.IndexOf(' ');
                    var cmd = (spaceIdx < 0 ? line : line[..spaceIdx]).ToUpperInvariant();
                    var arg = spaceIdx < 0 ? "" : line[(spaceIdx + 1)..];

                    switch (cmd)
                    {
                        case "USER":
                            await WriteLineAsync(controlStream, "331 Password required (any value accepted)", token);
                            break;
                        case "PASS":
                            await WriteLineAsync(controlStream, "230 Logged in (guest access)", token);
                            break;
                        case "SYST":
                            await WriteLineAsync(controlStream, "215 UNIX Type: L8", token);
                            break;
                        case "TYPE":
                            await WriteLineAsync(controlStream, "200 Type set", token);
                            break;
                        case "PWD":
                            await WriteLineAsync(controlStream, "257 \"/\" is current directory", token);
                            break;
                        case "CWD":
                            await WriteLineAsync(controlStream, "250 Directory changed", token);
                            break;
                        case "PASV":
                            dataListener?.Stop();
                            dataListener = new TcpListener(IPAddress.Any, 0);
                            dataListener.Start();
                            var dataPort = ((IPEndPoint)dataListener.LocalEndpoint).Port;
                            var ip = _localIpProvider() ?? IPAddress.Loopback;
                            var ipParts = ip.GetAddressBytes();
                            await WriteLineAsync(controlStream,
                                $"227 Entering Passive Mode ({ipParts[0]},{ipParts[1]},{ipParts[2]},{ipParts[3]},{dataPort / 256},{dataPort % 256})",
                                token);
                            break;
                        case "STOR":
                            await HandleStorAsync(controlStream, dataListener, arg, token);
                            break;
                        case "QUIT":
                            await WriteLineAsync(controlStream, "221 Goodbye", token);
                            return;
                        default:
                            await WriteLineAsync(controlStream, "202 Command not implemented, ignored", token);
                            break;
                    }
                }
            }
            catch (Exception ex)
            {
                ErrorOccurred?.Invoke($"FTP: خطأ في الاتصال: {ex.Message}");
            }
            finally
            {
                dataListener?.Stop();
            }
        }
    }

    private async Task HandleStorAsync(NetworkStream controlStream, TcpListener? dataListener, string fileName, CancellationToken token)
    {
        if (dataListener == null)
        {
            await WriteLineAsync(controlStream, "425 Use PASV first", token);
            return;
        }

        await WriteLineAsync(controlStream, "150 Opening data connection", token);

        try
        {
            using var dataClient = await dataListener.AcceptTcpClientAsync(token);
            using var dataStream = dataClient.GetStream();
            using var ms = new MemoryStream();
            await dataStream.CopyToAsync(ms, token);
            var bytes = ms.ToArray();

            await WriteLineAsync(controlStream, "226 Transfer complete", token);

            if (bytes.Length == 0) return;

            var printerName = _targetPrinterNameProvider();
            if (string.IsNullOrWhiteSpace(printerName))
            {
                ErrorOccurred?.Invoke("FTP: لم يتم اختيار طابعة هدف");
                return;
            }

            var ok = RawPrinterHelper.SendBytesToPrinter(printerName, bytes);
            if (ok)
                JobReceived?.Invoke($"FTP: تم استلام \"{fileName}\" ({bytes.Length:N0} بايت) وإرساله إلى \"{printerName}\"");
            else
                ErrorOccurred?.Invoke($"FTP: فشل إرسال \"{fileName}\" إلى \"{printerName}\"");
        }
        catch (Exception ex)
        {
            ErrorOccurred?.Invoke($"FTP: فشل استقبال الملف: {ex.Message}");
        }
    }

    private static async Task WriteLineAsync(NetworkStream stream, string line, CancellationToken token)
    {
        var bytes = Encoding.ASCII.GetBytes(line + "\r\n");
        await stream.WriteAsync(bytes, token);
    }

    private static async Task<string?> ReadLineAsync(NetworkStream stream, CancellationToken token)
    {
        var buffer = new List<byte>();
        var singleByte = new byte[1];
        while (true)
        {
            int read = await stream.ReadAsync(singleByte.AsMemory(0, 1), token);
            if (read == 0) return buffer.Count == 0 ? null : Encoding.ASCII.GetString(buffer.ToArray());
            if (singleByte[0] == (byte)'\n') break;
            if (singleByte[0] != (byte)'\r') buffer.Add(singleByte[0]);
        }
        return Encoding.ASCII.GetString(buffer.ToArray());
    }

    public void Dispose() => Stop();
}

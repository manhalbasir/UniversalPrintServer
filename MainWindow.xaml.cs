using System.Windows;
using UniversalPrintServer.Services;
using System.Windows.Controls;
namespace UniversalPrintServer;

public partial class MainWindow : Window
{
    private RawPrintListener? _rawListener;
    private IppServer? _ippServer;
    private LpdServer? _lpdServer;
    private FtpServer? _ftpServer;
    private List<Services.PrinterEntry> _printers = new();

    public MainWindow()
    {
        InitializeComponent();
        RefreshIps();
        RefreshPrinters();
        Log("جاهز. اختر الطابعة، ثم فعّل المشاركة المناسبة.");
    }

    // ------------------------------------------------------------ IP

    private void RefreshIps()
    {
        var ips = NetworkInfo.GetLocalIPv4Addresses()
            .Select(x => new { AdapterName = x.AdapterName, IpAddress = x.IpAddress })
            .ToList();
        IpListControl.ItemsSource = ips;

        if (ips.Count == 0)
            Log("⚠️ لم يتم العثور على أي عنوان IP على الشبكة المحلية. تأكد من الاتصال بشبكة Wi-Fi أو Ethernet.");
    }

    private void RefreshIp_Click(object sender, RoutedEventArgs e) => RefreshIps();

    private void CopyIpButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is string ip)
        {
            Clipboard.SetText(ip);
            Log($"تم نسخ العنوان: {ip}");
        }
    }

    // ------------------------------------------------------------ الطابعات

    private void RefreshPrinters()
    {
        _printers = PrinterManager.GetInstalledPrinters();
        PrinterComboBox.ItemsSource = _printers.Select(p => p.IsShared ? $"{p.Name}  (مُشارَكة: {p.ShareName})" : p.Name).ToList();
        if (_printers.Count > 0) PrinterComboBox.SelectedIndex = 0;

        if (_printers.Count == 0)
            Log("⚠️ لم يتم العثور على أي طابعة مثبَّتة على هذا الحاسوب.");
    }

    private void RefreshPrinters_Click(object sender, RoutedEventArgs e) => RefreshPrinters();

    private string? GetSelectedPrinterName()
    {
        int index = PrinterComboBox.SelectedIndex;
        if (index < 0 || index >= _printers.Count) return null;
        return _printers[index].Name;
    }

    // ------------------------------------------------------------ SMB

    private void EnableSmb_Click(object sender, RoutedEventArgs e)
    {
        var printerName = GetSelectedPrinterName();
        if (printerName == null) { Log("⚠️ اختر طابعة أولاً"); return; }

        EnableSmbButton.IsEnabled = false;
        var (success, message) = PrinterManager.EnableSharing(printerName);
        EnableSmbButton.IsEnabled = true;

        Log(message);
        SmbStatusText.Text = success ? "✅ مُفعّلة" : "❌ فشلت";
        SmbStatusText.Foreground = success
            ? System.Windows.Media.Brushes.Green
            : System.Windows.Media.Brushes.Red;

        if (success) RefreshPrinters();
    }

    private void DisableSmb_Click(object sender, RoutedEventArgs e)
    {
        var printerName = GetSelectedPrinterName();
        if (printerName == null) { Log("⚠️ اختر طابعة أولاً"); return; }

        var (success, message) = PrinterManager.DisableSharing(printerName);
        Log(message);
        SmbStatusText.Text = "غير مُفعّلة";
        SmbStatusText.Foreground = System.Windows.Media.Brushes.DarkOrange;

        if (success) RefreshPrinters();
    }

    private void EnableGuest_Click(object sender, RoutedEventArgs e)
    {
        var printerName = GetSelectedPrinterName();
        if (printerName == null) { Log("⚠️ اختر طابعة أولاً"); return; }

        GuestAccessButton.IsEnabled = false;
        var (success, message) = PrinterManager.EnableGuestAccess(printerName);
        GuestAccessButton.IsEnabled = true;
        Log((success ? "✅ " : "⚠️ ") + message);
    }

    // ------------------------------------------------------------ RAW/JetDirect

    private void StartRaw_Click(object sender, RoutedEventArgs e)
    {
        if (_rawListener?.IsRunning == true) return;

        var (fwSuccess, fwMessage) = FirewallHelper.OpenTcpPort(9100, "UniversalPrintServer RAW 9100");
        Log(fwMessage);

        _rawListener = new RawPrintListener(GetSelectedPrinterName, () => GdiModeCheckBox.IsChecked == true);
        _rawListener.JobReceived += msg => Dispatcher.Invoke(() => Log($"🖨️ {msg}"));
        _rawListener.ErrorOccurred += msg => Dispatcher.Invoke(() => Log($"❌ {msg}"));

        try
        {
            _rawListener.Start(9100);
            RawStatusText.Text = "✅ يعمل على المنفذ 9100";
            RawStatusText.Foreground = System.Windows.Media.Brushes.Green;
            StartRawButton.IsEnabled = false;
            StopRawButton.IsEnabled = true;
            Log("تم تشغيل خادم RAW/JetDirect على المنفذ 9100");
        }
        catch (Exception ex)
        {
            Log($"❌ فشل تشغيل الخادم: {ex.Message}");
        }
    }

    private void StopRaw_Click(object sender, RoutedEventArgs e)
    {
        _rawListener?.Stop();
        RawStatusText.Text = "متوقّف";
        RawStatusText.Foreground = System.Windows.Media.Brushes.DarkOrange;
        StartRawButton.IsEnabled = true;
        StopRawButton.IsEnabled = false;
        Log("تم إيقاف خادم RAW/JetDirect");
    }

    // ------------------------------------------------------------ IPP

    private void StartIpp_Click(object sender, RoutedEventArgs e)
    {
        if (_ippServer?.IsRunning == true) return;

        var (fwSuccess, fwMessage) = FirewallHelper.OpenTcpPort(631, "UniversalPrintServer IPP 631");
        Log(fwMessage);

        _ippServer = new IppServer(GetSelectedPrinterName);
        _ippServer.JobReceived += msg => Dispatcher.Invoke(() => Log($"🖨️ {msg}"));
        _ippServer.ErrorOccurred += msg => Dispatcher.Invoke(() => Log($"❌ {msg}"));

        try
        {
            _ippServer.Start(631);
            IppStatusText.Text = "✅ يعمل على المنفذ 631";
            IppStatusText.Foreground = System.Windows.Media.Brushes.Green;
            StartIppButton.IsEnabled = false;
            StopIppButton.IsEnabled = true;
            Log("تم تشغيل خادم IPP على المنفذ 631");
        }
        catch (Exception ex)
        {
            Log($"❌ فشل تشغيل خادم IPP: {ex.Message}");
        }
    }

    private void StopIpp_Click(object sender, RoutedEventArgs e)
    {
        _ippServer?.Stop();
        IppStatusText.Text = "متوقّف";
        IppStatusText.Foreground = System.Windows.Media.Brushes.DarkOrange;
        StartIppButton.IsEnabled = true;
        StopIppButton.IsEnabled = false;
        Log("تم إيقاف خادم IPP");
    }

    // ------------------------------------------------------------ LPR/LPD

    private void StartLpr_Click(object sender, RoutedEventArgs e)
    {
        if (_lpdServer?.IsRunning == true) return;

        var (fwSuccess, fwMessage) = FirewallHelper.OpenTcpPort(515, "UniversalPrintServer LPR 515");
        Log(fwMessage);

        _lpdServer = new LpdServer(GetSelectedPrinterName);
        _lpdServer.JobReceived += msg => Dispatcher.Invoke(() => Log($"🖨️ {msg}"));
        _lpdServer.ErrorOccurred += msg => Dispatcher.Invoke(() => Log($"❌ {msg}"));

        try
        {
            _lpdServer.Start(515);
            LprStatusText.Text = "✅ يعمل على المنفذ 515";
            LprStatusText.Foreground = System.Windows.Media.Brushes.Green;
            StartLprButton.IsEnabled = false;
            StopLprButton.IsEnabled = true;
            Log("تم تشغيل خادم LPR/LPD على المنفذ 515");
        }
        catch (Exception ex)
        {
            Log($"❌ فشل تشغيل خادم LPR: {ex.Message}");
        }
    }

    private void StopLpr_Click(object sender, RoutedEventArgs e)
    {
        _lpdServer?.Stop();
        LprStatusText.Text = "متوقّف";
        LprStatusText.Foreground = System.Windows.Media.Brushes.DarkOrange;
        StartLprButton.IsEnabled = true;
        StopLprButton.IsEnabled = false;
        Log("تم إيقاف خادم LPR/LPD");
    }

    // ------------------------------------------------------------ FTP

    private void StartFtp_Click(object sender, RoutedEventArgs e)
    {
        if (_ftpServer?.IsRunning == true) return;

        var (fwSuccess, fwMessage) = FirewallHelper.OpenTcpPort(21, "UniversalPrintServer FTP 21");
        Log(fwMessage);

        _ftpServer = new FtpServer(
            GetSelectedPrinterName,
            () =>
            {
                var ipText = NetworkInfo.GetBestGuessIp();
                return ipText != null ? System.Net.IPAddress.Parse(ipText) : null;
            });
        _ftpServer.JobReceived += msg => Dispatcher.Invoke(() => Log($"🖨️ {msg}"));
        _ftpServer.ErrorOccurred += msg => Dispatcher.Invoke(() => Log($"❌ {msg}"));

        try
        {
            _ftpServer.Start(21);
            FtpStatusText.Text = "✅ يعمل على المنفذ 21";
            FtpStatusText.Foreground = System.Windows.Media.Brushes.Green;
            StartFtpButton.IsEnabled = false;
            StopFtpButton.IsEnabled = true;
            Log("تم تشغيل خادم FTP على المنفذ 21");
        }
        catch (Exception ex)
        {
            Log($"❌ فشل تشغيل خادم FTP: {ex.Message}");
        }
    }

    private void StopFtp_Click(object sender, RoutedEventArgs e)
    {
        _ftpServer?.Stop();
        FtpStatusText.Text = "متوقّف";
        FtpStatusText.Foreground = System.Windows.Media.Brushes.DarkOrange;
        StartFtpButton.IsEnabled = true;
        StopFtpButton.IsEnabled = false;
        Log("تم إيقاف خادم FTP");
    }

    // ------------------------------------------------------------ سجل

    private void Log(string message)
    {
        LogTextBox.AppendText($"[{DateTime.Now:HH:mm:ss}] {message}\n");
        LogTextBox.ScrollToEnd();
    }

    protected override void OnClosed(EventArgs e)
    {
        _rawListener?.Stop();
        _ippServer?.Stop();
        _lpdServer?.Stop();
        _ftpServer?.Stop();
        base.OnClosed(e);
    }
}

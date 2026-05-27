using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using Microsoft.Web.WebView2.Core;
using WebVideoDownloader.Models;
using WebVideoDownloader.Services;

namespace WebVideoDownloader;

public partial class MainWindow : Form
{
    private const string DefaultUrl = "";
    private const string BrowserUserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/125.0 Safari/537.36";

    private readonly HttpClient _httpClient = new(new HttpClientHandler
    {
        AllowAutoRedirect = true,
        AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate | DecompressionMethods.Brotli
    });

    private readonly List<VideoCandidate> _candidates = [];
    private readonly HashSet<string> _candidateUrls = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _playerUrls = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _blobUrls = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, NetworkRequestInfo> _networkRequests = new();
    private readonly System.Windows.Forms.Timer _scanTimer = new();
    private readonly MediaUrlExtractor _mediaUrlExtractor;
    private readonly FfmpegRunner _ffmpegRunner;

    private CoreWebView2DevToolsProtocolEventReceiver? _requestWillBeSentReceiver;
    private CoreWebView2DevToolsProtocolEventReceiver? _responseReceivedReceiver;
    private CoreWebView2DevToolsProtocolEventReceiver? _loadingFinishedReceiver;
    private CancellationTokenSource? _downloadCts;
    private string _currentPageUrl = DefaultUrl;
    private string _downloadFolder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
    private bool _scanInProgress;
    private bool _webViewReady;
    private int _scanTimerTicks;
    private int _responseBodyProbeCount;
    private bool _isDarkMode;

    public MainWindow()
    {
        InitializeComponent();
        ApplyWindowIconFromExecutable();
        _mediaUrlExtractor = new MediaUrlExtractor(ResolveUrl);
        _ffmpegRunner = new FfmpegRunner(BrowserUserAgent, SetStatus, Log);
        _httpClient.Timeout = TimeSpan.FromHours(6);
        _scanTimer.Interval = 3000;
        _scanTimer.Tick += ScanTimer_Tick;
    }

    private void ApplyWindowIconFromExecutable()
    {
        try
        {
            using var executableIcon = Icon.ExtractAssociatedIcon(Application.ExecutablePath);
            if (executableIcon is not null)
            {
                Icon = (Icon)executableIcon.Clone();
            }
        }
        catch
        {
        }
    }

    private async void MainWindow_Load(object? sender, EventArgs e)
    {
        urlTextBox.Text = DefaultUrl;
        UpdateOutputFolderLabel();
        SetStatus("WebView2 초기화 중...");

        try
        {
            var userDataFolder = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "WebVideoDownloader",
                "WebView2");
            Directory.CreateDirectory(userDataFolder);

            var environment = await CoreWebView2Environment.CreateAsync(userDataFolder: userDataFolder);
            await webView.EnsureCoreWebView2Async(environment);

            if (webView.CoreWebView2 is null)
            {
                throw new InvalidOperationException("WebView2를 초기화하지 못했습니다.");
            }

            webView.CoreWebView2.Settings.AreDevToolsEnabled = true;
            webView.CoreWebView2.AddWebResourceRequestedFilter("*", CoreWebView2WebResourceContext.All);
            webView.CoreWebView2.WebResourceRequested += CoreWebView2_WebResourceRequested;
            webView.CoreWebView2.WebResourceResponseReceived += CoreWebView2_WebResourceResponseReceived;
            webView.CoreWebView2.NavigationCompleted += CoreWebView2_NavigationCompleted;
            webView.CoreWebView2.DocumentTitleChanged += CoreWebView2_DocumentTitleChanged;
            await webView.CoreWebView2.AddScriptToExecuteOnDocumentCreatedAsync(VideoProbeScripts.NetworkProbeInjection);
            await InitializeDevToolsNetworkCaptureAsync();

            _webViewReady = true;
            SetStatus("URL을 입력하고 열기를 누르세요.");
        }
        catch (Exception ex)
        {
            SetStatus("WebView2 초기화 실패");
            Log($"WebView2 초기화 실패: {ex.Message}");
            MessageBox.Show(
                this,
                "WebView2 런타임 초기화에 실패했습니다. Microsoft Edge WebView2 Runtime이 설치되어 있는지 확인하세요.\r\n\r\n" + ex.Message,
                "초기화 실패",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
        }
    }

    private void MainWindow_FormClosing(object? sender, FormClosingEventArgs e)
    {
        _downloadCts?.Cancel();
    }

    private void NavigateButton_Click(object? sender, EventArgs e)
    {
        NavigateToUrl(urlTextBox.Text);
    }

    private async void RescanButton_Click(object? sender, EventArgs e)
    {
        await ScanPageForVideoUrlsAsync();
    }

    private void ChooseFolderButton_Click(object? sender, EventArgs e)
    {
        using var dialog = new FolderBrowserDialog
        {
            Description = "동영상을 저장할 폴더를 선택하세요.",
            SelectedPath = _downloadFolder,
            UseDescriptionForTitle = true
        };

        if (dialog.ShowDialog(this) == DialogResult.OK && !string.IsNullOrWhiteSpace(dialog.SelectedPath))
        {
            _downloadFolder = dialog.SelectedPath;
            UpdateOutputFolderLabel();
            Log($"저장 폴더 변경: {_downloadFolder}");
        }
    }

    private void OpenFolderButton_Click(object? sender, EventArgs e)
    {
        try
        {
            Directory.CreateDirectory(_downloadFolder);
            Process.Start(new ProcessStartInfo
            {
                FileName = _downloadFolder,
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            Log($"폴더 열기 실패: {ex.Message}");
            MessageBox.Show(this, ex.Message, "폴더 열기 실패", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private async void DownloadButton_Click(object? sender, EventArgs e)
    {
        var candidate = GetSelectedCandidate();
        if (candidate is null)
        {
            MessageBox.Show(this, "다운로드할 동영상을 먼저 선택하세요.", "선택 필요", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        Directory.CreateDirectory(_downloadFolder);
        var outputPath = GetUniqueOutputPath(candidate);

        _downloadCts = new CancellationTokenSource();
        SetDownloadControls(isDownloading: true);
        SetProgress(0, indeterminate: candidate.Kind is VideoKind.Hls or VideoKind.Level5Hls);
        SetStatus("다운로드 준비 중...");

        try
        {
            if (candidate.Kind == VideoKind.Hls)
            {
                await DownloadHlsAsync(candidate, outputPath, _downloadCts.Token);
            }
            else if (candidate.Kind == VideoKind.Level5Hls)
            {
                await DownloadLevel5HlsAsync(candidate, outputPath, _downloadCts.Token);
            }
            else
            {
                await DownloadDirectFileAsync(candidate, outputPath, _downloadCts.Token);
            }

            SetProgress(100, indeterminate: false);
            SetStatus($"완료: {outputPath}");
            Log($"다운로드 완료: {outputPath}");
        }
        catch (OperationCanceledException)
        {
            TryDeletePartialFile(outputPath);
            SetProgress(0, indeterminate: false);
            SetStatus("다운로드 취소됨");
            Log("다운로드 취소됨");
        }
        catch (Exception ex)
        {
            SetProgress(0, indeterminate: false);
            SetStatus("다운로드 실패");
            Log($"다운로드 실패: {ex.Message}");
            MessageBox.Show(this, ex.Message, "다운로드 실패", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            _downloadCts.Dispose();
            _downloadCts = null;
            SetDownloadControls(isDownloading: false);
        }
    }

    private void CancelButton_Click(object? sender, EventArgs e)
    {
        _downloadCts?.Cancel();
    }

    private void ThemeToggleButton_Click(object? sender, EventArgs e)
    {
        _isDarkMode = !_isDarkMode;
        ApplyTheme();
    }

    private void CandidatesListView_ItemSelectionChanged(object? sender, ListViewItemSelectionChangedEventArgs e)
    {
        downloadButton.Enabled = _downloadCts is null && candidatesListView.SelectedItems.Count > 0;
    }

}



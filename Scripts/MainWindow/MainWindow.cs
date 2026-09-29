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
    private const string CaptureCandidateUrl = "wvd-capture://mediasource";

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
    private readonly MediaCaptureSink _captureSink;

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
        InitializeRangeControls();
        ApplyWindowIconFromExecutable();
        _mediaUrlExtractor = new MediaUrlExtractor(ResolveUrl);
        _ffmpegRunner = new FfmpegRunner(BrowserUserAgent, SetStatus, Log);
        _captureSink = new MediaCaptureSink(Log);
        _captureSink.CaptureStarted += OnCaptureStarted;
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
            await InstallMediaCaptureHookAsync();
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

    /// <summary>
    /// 재생 캡처 훅을 모든 프레임에 주입합니다. 싱크를 못 여는 환경이면 나머지 기능은 그대로 동작합니다.
    /// </summary>
    private async Task InstallMediaCaptureHookAsync()
    {
        try
        {
            _captureSink.Start();
            await webView.CoreWebView2!.AddScriptToExecuteOnDocumentCreatedAsync(
                VideoProbeScripts.BuildMediaSourceCaptureScript(_captureSink.Port, _captureSink.Token));
            Log($"재생 캡처 준비 완료 (127.0.0.1:{_captureSink.Port}). 영상을 재생하면 자동으로 모읍니다.");
        }
        catch (Exception ex)
        {
            Log($"재생 캡처를 준비하지 못했습니다: {ex.Message}");
        }
    }

    private void OnCaptureStarted()
    {
        Log("재생 캡처 시작. 영상을 끝까지 재생해야 전체가 저장됩니다.");
        AddOrUpdateCaptureCandidate();
    }

    private void MainWindow_FormClosing(object? sender, FormClosingEventArgs e)
    {
        _downloadCts?.Cancel();
        _captureSink.Dispose();
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

        DownloadRange? range = null;
        try
        {
            if (_rangeEnabled.Checked)
                range = DownloadRange.Parse(_rangeStart.Text, _rangeEnd.Text);
        }
        catch (FormatException ex)
        {
            MessageBox.Show(this, ex.Message, "구간 입력 확인", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        Directory.CreateDirectory(_downloadFolder);
        var finalPath = GetUniqueOutputPath(candidate);
        if (range is not null)
            finalPath = Path.Combine(_downloadFolder, Path.GetFileNameWithoutExtension(finalPath) + "_clip_" + Guid.NewGuid().ToString("N")[..8] + ".mp4");
        var outputPath = range is null ? finalPath : Path.Combine(_downloadFolder, ".wvd-" + Guid.NewGuid().ToString("N") + Path.GetExtension(GetUniqueOutputPath(candidate)));

        _downloadCts = new CancellationTokenSource();
        SetDownloadControls(isDownloading: true);
        SetProgress(0, indeterminate: candidate.Kind is VideoKind.Hls or VideoKind.Level5Hls or VideoKind.MediaCapture);
        SetStatus("다운로드 준비 중...");

        try
        {
            if (candidate.Kind == VideoKind.MediaCapture)
            {
                await SaveCapturedMediaAsync(outputPath, _downloadCts.Token);
            }
            else if (candidate.Kind == VideoKind.Hls)
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

            if (range is not null)
            {
                SetProgress(0, indeterminate: true);
                await _ffmpegRunner.TrimAsync(outputPath, finalPath, range, _downloadCts.Token);
            }
            SetProgress(100, indeterminate: false);
            SetStatus($"완료: {finalPath}");
            Log($"다운로드 완료: {finalPath}");
        }
        catch (OperationCanceledException)
        {
            TryDeletePartialFile(outputPath);
            TryDeletePartialFile(finalPath);
            SetProgress(0, indeterminate: false);
            SetStatus("다운로드 취소됨");
            Log("다운로드 취소됨");
        }
        catch (Exception ex)
        {
            TryDeletePartialFile(outputPath);
            TryDeletePartialFile(finalPath);
            SetProgress(0, indeterminate: false);
            SetStatus("다운로드 실패");
            Log($"다운로드 실패: {ex.Message}");
            MessageBox.Show(this, ex.Message, "다운로드 실패", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            if (range is not null) TryDeletePartialFile(outputPath);
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



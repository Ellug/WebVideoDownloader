#pragma warning disable CS8600

using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Microsoft.Web.WebView2.Core;

namespace WebVideoDownloader;

public partial class MainWindow
{
    private static string GetJsonString(JsonElement element, string propertyName)
    {
        JsonElement value = default(JsonElement);
        bool flag = element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(propertyName, out value);
        bool flag2 = flag;
        if (!flag2)
        {
            JsonValueKind valueKind = value.ValueKind;
            bool flag3 = ((valueKind == JsonValueKind.Undefined || valueKind == JsonValueKind.Null) ? true : false);
            flag2 = flag3;
        }
        if (flag2)
        {
            return "";
        }
        return (value.ValueKind == JsonValueKind.String) ? (value.GetString() ?? "") : value.ToString();
    }

    private static string GetHeaderFromDevToolsResponse(JsonElement response, string headerName)
    {
        if (!response.TryGetProperty("headers", out var value) || value.ValueKind != JsonValueKind.Object)
        {
            return "";
        }
        foreach (JsonProperty item in value.EnumerateObject())
        {
            if (item.Name.Equals(headerName, StringComparison.OrdinalIgnoreCase))
            {
                return (item.Value.ValueKind == JsonValueKind.String) ? (item.Value.GetString() ?? "") : item.Value.ToString();
            }
        }
        return "";
    }

    private static string? FindNodeExecutable()
    {
        string text = Environment.GetEnvironmentVariable("PATH") ?? "";
        string[] array = text.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries);
        foreach (string text2 in array)
        {
            try
            {
                string text3 = Path.Combine(text2.Trim(), "node.exe");
                if (File.Exists(text3))
                {
                    return text3;
                }
            }
            catch
            {
            }
        }
        string[] source = new string[2]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "nodejs", "node.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "nodejs", "node.exe")
        };
        return source.FirstOrDefault(File.Exists);
    }

    private static string TryGetHeader(CoreWebView2HttpResponseHeaders headers, string name)
    {
        try
        {
            return headers.GetHeader(name) ?? "";
        }
        catch
        {
            return "";
        }
    }

    private static IReadOnlyList<string> DeserializeStringArray(string scriptResult)
    {
        if (string.IsNullOrWhiteSpace(scriptResult) || scriptResult == "null")
        {
            return Array.Empty<string>();
        }
        try
        {
            return JsonSerializer.Deserialize<List<string>>(scriptResult) ?? new List<string>();
        }
        catch
        {
            return Array.Empty<string>();
        }
    }

    private static string DeserializeJsonString(string scriptResult)
    {
        if (string.IsNullOrWhiteSpace(scriptResult) || scriptResult == "null")
        {
            return "";
        }
        try
        {
            return JsonSerializer.Deserialize<string>(scriptResult) ?? "";
        }
        catch
        {
            return "";
        }
    }

    private static string GetDirectDownloadExtension(string url)
    {
        if (Uri.TryCreate(url, UriKind.Absolute, out Uri result))
        {
            string extension = Path.GetExtension(result.AbsolutePath);
            if (!string.IsNullOrWhiteSpace(extension) && extension.Length <= 6 && extension.Any(char.IsLetterOrDigit))
            {
                return extension;
            }
        }
        return ".mp4";
    }

    private static string SanitizeFileName(string value)
    {
        char[] invalidFileNameChars = Path.GetInvalidFileNameChars();
        StringBuilder stringBuilder = new StringBuilder(value.Length);
        foreach (char c in value)
        {
            stringBuilder.Append(Enumerable.Contains(invalidFileNameChars, c) ? '_' : c);
        }
        string text = stringBuilder.ToString().Trim();
        return string.IsNullOrWhiteSpace(text) ? "video" : text;
    }

    private static string GetOrigin(Uri uri)
    {
        return uri.Scheme + "://" + uri.Host + (uri.IsDefaultPort ? "" : $":{uri.Port}");
    }

    private static string FormatBytes(long bytes)
    {
        string[] array = new string[5] { "B", "KB", "MB", "GB", "TB" };
        double num = bytes;
        int num2 = 0;
        while (num >= 1024.0 && num2 < array.Length - 1)
        {
            num /= 1024.0;
            num2++;
        }
        return $"{num:0.##} {array[num2]}";
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch
        {
        }
    }

    private static void TryDeletePartialFile(string outputPath)
    {
        try
        {
            if (File.Exists(outputPath))
            {
                File.Delete(outputPath);
            }
        }
        catch
        {
        }
    }

    private static void TryDeleteDirectory(string directoryPath)
    {
        try
        {
            if (Directory.Exists(directoryPath))
            {
                Directory.Delete(directoryPath, recursive: true);
            }
        }
        catch
        {
        }
    }

    private void ApplyTheme()
    {
        Color bg         = _isDarkMode ? Color.FromArgb(28, 28, 28)  : SystemColors.Control;
        Color fg         = _isDarkMode ? Color.FromArgb(220, 220, 220) : SystemColors.ControlText;
        Color inputBg    = _isDarkMode ? Color.FromArgb(45, 45, 45)  : SystemColors.Window;
        Color inputFg    = _isDarkMode ? Color.FromArgb(220, 220, 220) : SystemColors.WindowText;
        Color btnBg      = _isDarkMode ? Color.FromArgb(55, 55, 55)  : SystemColors.Control;
        Color btnFg      = _isDarkMode ? Color.FromArgb(210, 210, 210) : SystemColors.ControlText;
        Color btnBorder  = _isDarkMode ? Color.FromArgb(80, 80, 80)  : SystemColors.ControlDark;
        Color splitterBg = _isDarkMode ? Color.FromArgb(50, 50, 50)  : SystemColors.ControlDark;

        BackColor = bg;
        ForeColor = fg;

        foreach (Control c in new Control[]
        {
            rootLayout, topLayout, rightLayout, statusLayout,
            splitContainer, splitContainer.Panel1, splitContainer.Panel2
        })
        {
            c.BackColor = bg;
            c.ForeColor = fg;
        }

        splitContainer.BackColor = splitterBg;
        splitContainer.Panel1.BackColor = bg;
        splitContainer.Panel2.BackColor = bg;

        foreach (Label lbl in new[] { urlLabel, candidatesLabel, statusLabel, outputFolderLabel })
        {
            lbl.BackColor = bg;
            lbl.ForeColor = fg;
        }

        urlTextBox.BackColor = inputBg;
        urlTextBox.ForeColor = inputFg;
        urlTextBox.BorderStyle = BorderStyle.FixedSingle;

        logTextBox.BackColor = _isDarkMode ? Color.FromArgb(22, 22, 22) : SystemColors.Window;
        logTextBox.ForeColor = _isDarkMode ? Color.FromArgb(180, 180, 180) : SystemColors.WindowText;
        logTextBox.BorderStyle = BorderStyle.FixedSingle;

        candidatesListView.BackColor = inputBg;
        candidatesListView.ForeColor = inputFg;

        foreach (Button btn in new[] { navigateButton, rescanButton, chooseFolderButton,
            openFolderButton, downloadButton, cancelButton, themeToggleButton })
        {
            btn.UseVisualStyleBackColor = false;
            btn.BackColor = btnBg;
            btn.ForeColor = btnFg;
            btn.FlatStyle = _isDarkMode ? FlatStyle.Flat : FlatStyle.Standard;
            if (_isDarkMode)
                btn.FlatAppearance.BorderColor = btnBorder;
        }

        themeToggleButton.Text = _isDarkMode ? "☀" : "🌙";
        webView.DefaultBackgroundColor = _isDarkMode ? Color.FromArgb(28, 28, 28) : Color.White;

        RenderCandidateList();
    }

    private void SetDownloadControls(bool isDownloading)
    {
        navigateButton.Enabled = !isDownloading;
        rescanButton.Enabled = !isDownloading;
        chooseFolderButton.Enabled = !isDownloading;
        openFolderButton.Enabled = !isDownloading;
        downloadButton.Enabled = !isDownloading && candidatesListView.SelectedItems.Count > 0;
        cancelButton.Enabled = isDownloading;
    }

    private void SetProgress(int value, bool indeterminate)
    {
        if (base.InvokeRequired)
        {
            BeginInvoke(delegate
            {
                SetProgress(value, indeterminate);
            });
        }
        else
        {
            progressBar.Style = (indeterminate ? ProgressBarStyle.Marquee : ProgressBarStyle.Blocks);
            progressBar.MarqueeAnimationSpeed = (indeterminate ? 30 : 0);
            progressBar.Value = Math.Clamp(value, progressBar.Minimum, progressBar.Maximum);
        }
    }

    private void SetStatus(string message)
    {
        if (base.InvokeRequired)
        {
            BeginInvoke(delegate
            {
                SetStatus(message);
            });
        }
        else
        {
            statusLabel.Text = message;
        }
    }

    private void UpdateOutputFolderLabel()
    {
        outputFolderLabel.Text = "저장: " + _downloadFolder;
        outputFolderLabel.Tag = _downloadFolder;
    }

    private void Log(string message)
    {
        if (base.InvokeRequired)
        {
            BeginInvoke(delegate
            {
                Log(message);
            });
            return;
        }
        logTextBox.AppendText($"[{DateTime.Now:HH:mm:ss}] {message}{Environment.NewLine}");
    }
}






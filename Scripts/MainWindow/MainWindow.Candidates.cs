#pragma warning disable CS8600

using WebVideoDownloader.Models;
using WebVideoDownloader.Services;

namespace WebVideoDownloader;

public partial class MainWindow
{
    private void AddCandidate(string rawUrl, string source, string contentType, VideoKind? kindOverride = null, string? refererOverride = null, string? wasmJsUrl = null, string? wasmBinUrl = null, string? expectedWasmSha384Hex = null, string? capturedManifestText = null)
    {
        if (base.InvokeRequired)
        {
            BeginInvoke(delegate
            {
                AddCandidate(rawUrl, source, contentType, kindOverride, refererOverride, wasmJsUrl, wasmBinUrl, expectedWasmSha384Hex, capturedManifestText);
            });
            return;
        }
        string text = ResolveUrl(rawUrl);
        if (string.IsNullOrWhiteSpace(text) || text.StartsWith("blob:", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }
        VideoKind videoKind = kindOverride ?? MediaClassifier.DetermineVideoKind(text, contentType);
        if (videoKind != VideoKind.Unknown)
        {
            string text2 = UrlTools.NormalizeCandidateUrl(text);
            if (!_candidateUrls.Add(text2))
            {
                TryUpgradeCandidate(text2, videoKind, source, contentType, refererOverride, wasmJsUrl, wasmBinUrl, expectedWasmSha384Hex, capturedManifestText);
                return;
            }
            VideoCandidate videoCandidate = new VideoCandidate(text2, videoKind, source, contentType, refererOverride ?? _currentPageUrl, DateTime.Now, wasmJsUrl, wasmBinUrl, expectedWasmSha384Hex, capturedManifestText);
            _candidates.Add(videoCandidate);
            VideoCandidate selectedCandidate = GetSelectedCandidate();
            string preferredUrl = (((object)selectedCandidate == null || CandidateDisplayService.GetDisplayInfo(videoCandidate).Priority > CandidateDisplayService.GetDisplayInfo(selectedCandidate).Priority) ? videoCandidate.Url : selectedCandidate.Url);
            RenderCandidateList(preferredUrl);
            downloadButton.Enabled = _downloadCts == null && candidatesListView.SelectedItems.Count > 0;
            SetStatus($"영상 후보 {candidatesListView.Items.Count}개 감지");
            CandidateDisplayInfo displayInfo = CandidateDisplayService.GetDisplayInfo(videoCandidate);
            Log($"영상 후보 추가 [{displayInfo.Recommendation}/{videoCandidate.KindLabel}/{displayInfo.QualityLabel}/{displayInfo.HostLabel}/{source}]: {videoCandidate.Url}");
        }
    }

    private void TryUpgradeCandidate(string normalizedUrl, VideoKind kind, string source, string contentType, string? refererOverride, string? wasmJsUrl, string? wasmBinUrl, string? expectedWasmSha384Hex, string? capturedManifestText)
    {
        if ((kind != VideoKind.Level5Hls || string.IsNullOrWhiteSpace(wasmJsUrl) || string.IsNullOrWhiteSpace(wasmBinUrl)) && string.IsNullOrWhiteSpace(capturedManifestText))
        {
            return;
        }
        for (int i = 0; i < _candidates.Count; i++)
        {
            VideoCandidate videoCandidate = _candidates[i];
            if (videoCandidate.Url.Equals(normalizedUrl, StringComparison.OrdinalIgnoreCase))
            {
                bool flag = kind == VideoKind.Level5Hls && !string.IsNullOrWhiteSpace(wasmJsUrl) && !string.IsNullOrWhiteSpace(wasmBinUrl) && string.IsNullOrWhiteSpace(videoCandidate.WasmJsUrl);
                bool flag2 = !string.IsNullOrWhiteSpace(capturedManifestText) && string.IsNullOrWhiteSpace(videoCandidate.CapturedManifestText);
                if (flag || flag2)
                {
                    VideoCandidate videoCandidate2 = videoCandidate with
                    {
                        Kind = (flag ? VideoKind.Level5Hls : videoCandidate.Kind),
                        Source = source,
                        ContentType = (string.IsNullOrWhiteSpace(contentType) ? videoCandidate.ContentType : contentType),
                        Referer = (refererOverride ?? videoCandidate.Referer),
                        WasmJsUrl = (flag ? wasmJsUrl : videoCandidate.WasmJsUrl),
                        WasmBinUrl = (flag ? wasmBinUrl : videoCandidate.WasmBinUrl),
                        ExpectedWasmSha384Hex = (flag ? expectedWasmSha384Hex : videoCandidate.ExpectedWasmSha384Hex),
                        CapturedManifestText = (flag2 ? capturedManifestText : videoCandidate.CapturedManifestText)
                    };
                    _candidates[i] = videoCandidate2;
                    RenderCandidateList(videoCandidate2.Url);
                    CandidateDisplayInfo displayInfo = CandidateDisplayService.GetDisplayInfo(videoCandidate2);
                    Log($"영상 후보 보강 [{displayInfo.Recommendation}/{videoCandidate2.KindLabel}/{displayInfo.QualityLabel}/{displayInfo.HostLabel}/{source}]: {videoCandidate2.Url}");
                }
                break;
            }
        }
    }

    private void MarkPlaybackUrl(string rawUrl, string signalSource)
    {
        if (base.InvokeRequired)
        {
            BeginInvoke(delegate
            {
                MarkPlaybackUrl(rawUrl, signalSource);
            });
        }
        else
        {
            if (string.IsNullOrWhiteSpace(rawUrl))
            {
                return;
            }
            if (rawUrl.StartsWith("blob:", StringComparison.OrdinalIgnoreCase))
            {
                _blobUrls.Add(rawUrl);
                return;
            }
            string text = ResolveUrl(rawUrl);
            if (string.IsNullOrWhiteSpace(text))
            {
                return;
            }
            int num = FindPlaybackCandidateIndex(text);
            if (num < 0)
            {
                VideoKind videoKind = MediaClassifier.DetermineVideoKind(text, "");
                if (videoKind != VideoKind.Unknown)
                {
                    AddCandidate(text, "재생중", "", videoKind);
                    num = FindPlaybackCandidateIndex(text);
                }
            }
            if (num >= 0)
            {
                DateTime now = DateTime.Now;
                VideoCandidate videoCandidate = _candidates[num];
                bool flag = CandidateDisplayService.IsCurrentlyPlaying(videoCandidate);
                VideoCandidate videoCandidate2 = videoCandidate with
                {
                    LastPlaybackSignalAt = now,
                    PlaybackSignalSource = signalSource
                };
                _candidates[num] = videoCandidate2;
                ExpirePlaybackSignals(render: false);
                RenderCandidateList(videoCandidate2.Url);
                if (!flag)
                {
                    Log($"현재 재생 후보 표시 [{videoCandidate2.KindLabel}/{signalSource}]: {videoCandidate2.Url}");
                }
            }
        }
    }

    private int FindPlaybackCandidateIndex(string mediaUrl)
    {
        string value = UrlTools.NormalizeCandidateUrl(mediaUrl);
        for (int i = 0; i < _candidates.Count; i++)
        {
            if (_candidates[i].Url.Equals(value, StringComparison.OrdinalIgnoreCase))
            {
                return i;
            }
        }
        int num = -1;
        int num2 = 0;
        for (int j = 0; j < _candidates.Count; j++)
        {
            int num3 = ScorePlaybackCandidate(_candidates[j], mediaUrl);
            if (num3 > num2)
            {
                num2 = num3;
                num = j;
            }
        }
        return (num2 >= 40) ? num : (-1);
    }

    private static int ScorePlaybackCandidate(VideoCandidate candidate, string mediaUrl)
    {
        VideoKind kind = candidate.Kind;
        if ((uint)(kind - 1) > 1u)
        {
            return 0;
        }
        if (!Uri.TryCreate(mediaUrl, UriKind.Absolute, out Uri result) || !Uri.TryCreate(candidate.Url, UriKind.Absolute, out Uri result2))
        {
            return 0;
        }
        int num = 0;
        if (result.Host.Equals(result2.Host, StringComparison.OrdinalIgnoreCase))
        {
            num += 25;
        }
        string directoryUrl = UrlTools.GetDirectoryUrl(candidate.Url);
        if (!string.IsNullOrWhiteSpace(directoryUrl) && mediaUrl.StartsWith(directoryUrl, StringComparison.OrdinalIgnoreCase))
        {
            num += 120;
        }
        string fileName = Path.GetFileName(result.AbsolutePath);
        if (!string.IsNullOrWhiteSpace(fileName) && !string.IsNullOrWhiteSpace(candidate.CapturedManifestText) && candidate.CapturedManifestText.Contains(fileName, StringComparison.OrdinalIgnoreCase))
        {
            num += 160;
        }
        string directoryUrl2 = UrlTools.GetDirectoryUrl(mediaUrl);
        if (!string.IsNullOrWhiteSpace(directoryUrl2) && candidate.Url.StartsWith(directoryUrl2, StringComparison.OrdinalIgnoreCase))
        {
            num += 80;
        }
        return num;
    }

    private void ExpirePlaybackSignals(bool render = true)
    {
        DateTime now = DateTime.Now;
        bool flag = false;
        for (int i = 0; i < _candidates.Count; i++)
        {
            VideoCandidate videoCandidate = _candidates[i];
            if (videoCandidate.LastPlaybackSignalAt.HasValue && !(now - videoCandidate.LastPlaybackSignalAt.Value <= CandidateDisplayService.PlaybackSignalLifetime))
            {
                _candidates[i] = videoCandidate with
                {
                    LastPlaybackSignalAt = null,
                    PlaybackSignalSource = null
                };
                flag = true;
            }
        }
        if (flag && render)
        {
            RenderCandidateList();
        }
    }

    private void RenderCandidateList(string? preferredUrl = null)
    {
        string text = preferredUrl ?? GetSelectedCandidate()?.Url;
        List<VideoCandidate> list = (from candidate in _candidates
                                     orderby CandidateDisplayService.GetDisplayInfo(candidate).Priority descending, CandidateDisplayService.GetDisplayInfo(candidate).Height.GetValueOrDefault() descending, candidate.DetectedAt
                                     select candidate).ToList();
        candidatesListView.BeginUpdate();
        try
        {
            candidatesListView.Items.Clear();
            ListViewItem listViewItem = null;
            for (int num = 0; num < list.Count; num++)
            {
                ListViewItem listViewItem2 = CreateCandidateListItem(list[num], num);
                candidatesListView.Items.Add(listViewItem2);
                if (text != null && listViewItem2.Tag is VideoCandidate videoCandidate && videoCandidate.Url.Equals(text, StringComparison.OrdinalIgnoreCase))
                {
                    listViewItem = listViewItem2;
                }
            }
            if (listViewItem == null)
            {
                listViewItem = ((candidatesListView.Items.Count > 0) ? candidatesListView.Items[0] : null);
            }
            if (listViewItem != null)
            {
                listViewItem.Selected = true;
                listViewItem.Focused = true;
                candidatesListView.EnsureVisible(listViewItem.Index);
            }
        }
        finally
        {
            candidatesListView.EndUpdate();
        }
    }

    private ListViewItem CreateCandidateListItem(VideoCandidate candidate, int rank)
    {
        CandidateDisplayInfo displayInfo = CandidateDisplayService.GetDisplayInfo(candidate);
        ListViewItem listViewItem = new ListViewItem(CandidateDisplayService.IsCurrentlyPlaying(candidate) ? "재생중" : ((rank == 0) ? "추천 1순위" : displayInfo.Recommendation));
        listViewItem.SubItems.Add(candidate.KindLabel);
        listViewItem.SubItems.Add(displayInfo.QualityLabel);
        listViewItem.SubItems.Add(displayInfo.HostLabel);
        listViewItem.SubItems.Add(candidate.Source);
        listViewItem.SubItems.Add(displayInfo.ShortUrl);
        listViewItem.SubItems.Add(CandidateDisplayService.ShortenContentType(candidate.ContentType));
        listViewItem.Tag = candidate;
        listViewItem.ToolTipText = candidate.Url;
        if (CandidateDisplayService.IsCurrentlyPlaying(candidate))
        {
            listViewItem.BackColor = _isDarkMode
                ? Color.FromArgb(20, 60, 30)
                : Color.FromArgb(222, 246, 226);
            listViewItem.ForeColor = _isDarkMode
                ? Color.FromArgb(100, 220, 130)
                : Color.FromArgb(20, 94, 43);
        }
        return listViewItem;
    }

    private VideoCandidate? GetSelectedCandidate()
    {
        if (candidatesListView.SelectedItems.Count == 0)
        {
            return null;
        }
        return candidatesListView.SelectedItems[0].Tag as VideoCandidate;
    }
}






namespace WebVideoDownloader.Services;

internal static class VideoProbeScripts
{
    public const string NetworkProbeInjection = """
        (() => {
            if (window.__wvdProbeInstalled) {
                return;
            }

            window.__wvdProbeInstalled = true;
            const state = window.__wvdProbe = window.__wvdProbe || { urls: [], hls: [] };
            const mediaUrlPattern = /(?:(?:https?:)?\/\/|\/)[^\s'"<>\\]+?(?:(?:\.(?:m3u8|mp4|webm|m4v|mov)(?:\?[^\s'"<>\\]*)?)|(?:\/v\.html\?[^\s'"<>\\]*))/ig;
            const hlsContentPattern = /mpegurl|application\/vnd\.apple/i;

            const remember = (list, value) => {
                try {
                    if (!value || typeof value !== 'string') {
                        return;
                    }

                    if (!list.includes(value) && list.length < 500) {
                        list.push(value);
                    }
                } catch {}
            };

            const post = (kind, value) => {
                try {
                    if (window.top && window.top !== window) {
                        window.top.postMessage({ __wvdProbeKind: kind, __wvdProbeValue: value }, '*');
                    }
                } catch {}
            };

            const addUrl = (value) => {
                if (!value) {
                    return;
                }

                value = String(value);
                remember(state.urls, value);
                post('url', value);
            };

            const addHls = (value) => {
                if (!value) {
                    return;
                }

                value = String(value);
                remember(state.hls, value);
                post('hls', value);
            };

            window.addEventListener('message', (event) => {
                try {
                    const data = event.data;
                    if (!data || data.__wvdProbeValue == null) {
                        return;
                    }

                    if (data.__wvdProbeKind === 'hls') {
                        remember(state.hls, String(data.__wvdProbeValue));
                    } else if (data.__wvdProbeKind === 'url') {
                        remember(state.urls, String(data.__wvdProbeValue));
                    }
                } catch {}
            });

            const looksLikeHlsManifest = (text) =>
                typeof text === 'string' && /^\s*#EXTM3U/i.test(text) && /#EXT-X-/i.test(text);

            const scanTextForMediaUrls = (text) => {
                if (!text || typeof text !== 'string' || text.length > 3000000) {
                    return;
                }

                mediaUrlPattern.lastIndex = 0;
                const matches = text.match(mediaUrlPattern);
                if (!matches) {
                    return;
                }

                matches.forEach(addUrl);
            };

            const shouldReadBody = (url, contentType, contentLength) =>
                !(contentLength && contentLength > 2000000) && (
                hlsContentPattern.test(contentType || '') ||
                /text|json|javascript|mpegurl|application\/vnd\.apple|octet-stream/i.test(contentType || '') ||
                /\.(m3u8|mp4|webm|m4v|mov)(\?|#|$)/i.test(url || ''));

            const inspectFetchResponse = (url, response) => {
                try {
                    const responseUrl = response && response.url ? response.url : url;
                    const contentType = response && response.headers ? response.headers.get('content-type') || '' : '';
                    const contentLength = response && response.headers ? Number(response.headers.get('content-length') || 0) : 0;
                    if (hlsContentPattern.test(contentType)) {
                        addHls(responseUrl);
                    }

                    if (!response || !shouldReadBody(responseUrl, contentType, contentLength)) {
                        return;
                    }

                    response.clone().text().then((text) => {
                        if (looksLikeHlsManifest(text)) {
                            addHls(responseUrl);
                        }

                        scanTextForMediaUrls(text);
                    }).catch(() => {});
                } catch {}
            };

            const originalFetch = window.fetch;
            if (typeof originalFetch === 'function') {
                window.fetch = function(input, init) {
                    const url = typeof input === 'string' ? input : input && input.url;
                    addUrl(url);
                    return originalFetch.apply(this, arguments).then((response) => {
                        inspectFetchResponse(url, response);
                        return response;
                    });
                };
            }

            if (window.XMLHttpRequest && XMLHttpRequest.prototype) {
                const originalOpen = XMLHttpRequest.prototype.open;
                const originalSend = XMLHttpRequest.prototype.send;

                XMLHttpRequest.prototype.open = function(method, url) {
                    this.__wvdUrl = url ? String(url) : '';
                    addUrl(this.__wvdUrl);
                    return originalOpen.apply(this, arguments);
                };

                XMLHttpRequest.prototype.send = function() {
                    try {
                        this.addEventListener('loadend', () => {
                            try {
                                const url = this.responseURL || this.__wvdUrl || '';
                                const contentType = this.getResponseHeader('content-type') || '';
                                const contentLength = Number(this.getResponseHeader('content-length') || 0);
                                if (hlsContentPattern.test(contentType)) {
                                    addHls(url);
                                }

                                if ((this.responseType === '' || this.responseType === 'text') && shouldReadBody(url, contentType, contentLength)) {
                                    const text = this.responseText || '';
                                    if (looksLikeHlsManifest(text)) {
                                        addHls(url);
                                    }

                                    scanTextForMediaUrls(text);
                                }
                            } catch {}
                        });
                    } catch {}

                    return originalSend.apply(this, arguments);
                };
            }
        })();
        """;

    public const string VideoProbe = """
        (() => {
            const urls = new Set();
            const add = (value) => {
                if (value && typeof value === 'string') {
                    urls.add(value);
                }
            };

            const addHls = (value) => {
                if (value && typeof value === 'string') {
                    urls.add('wvd-hls:' + value);
                }
            };

            const addPlaying = (value) => {
                if (value && typeof value === 'string') {
                    urls.add('wvd-playing:' + value);
                }
            };

            try {
                const probe = window.__wvdProbe;
                if (probe) {
                    (probe.urls || []).forEach(add);
                    (probe.hls || []).forEach(addHls);
                }
            } catch {}

            document.querySelectorAll('video').forEach((video) => {
                add(video.currentSrc);
                add(video.src);
                add(video.getAttribute('src'));

                if (!video.paused && !video.ended && video.readyState >= 2) {
                    addPlaying(video.currentSrc || video.src || video.getAttribute('src'));
                }

                video.querySelectorAll('source').forEach((source) => {
                    add(source.currentSrc);
                    add(source.src);
                    add(source.getAttribute('src'));
                });
            });

            document.querySelectorAll('source[src], a[href]').forEach((element) => {
                add(element.src);
                add(element.href);
                add(element.getAttribute('src'));
                add(element.getAttribute('href'));
            });

            document.querySelectorAll('iframe').forEach((frame) => {
                add(frame.src);
                try {
                    const doc = frame.contentDocument;
                    if (doc) {
                        doc.querySelectorAll('video, source[src], a[href]').forEach((element) => {
                            add(element.currentSrc);
                            add(element.src);
                            add(element.href);
                            add(element.getAttribute('src'));
                            add(element.getAttribute('href'));

                            if (element.tagName && element.tagName.toLowerCase() === 'video' &&
                                !element.paused && !element.ended && element.readyState >= 2) {
                                addPlaying(element.currentSrc || element.src || element.getAttribute('src'));
                            }
                        });
                    }
                } catch {}
            });

            performance.getEntriesByType('resource').forEach((entry) => add(entry.name));

            return Array.from(urls).filter((url) =>
                /(\.m3u8|\.mp4|\.webm|\.m4v|\.mov)(\?|#|$)|\/player\.php\?k=|\/v\.html\?/i.test(url) ||
                url.startsWith('wvd-hls:') ||
                url.startsWith('wvd-playing:') ||
                url.startsWith('blob:')
            );
        })()
        """;

    public const string Level5Decoder = """
        import fs from 'fs/promises';
        import init, * as runtime from './runtime.mjs';

        const wasmPath = process.argv[2];
        const keysPath = process.argv[3];
        const wasmBytes = await fs.readFile(wasmPath);
        await init(wasmBytes);

        const inputs = JSON.parse(await fs.readFile(keysPath, 'utf8'));
        const decoderNames = ['decode_session', 'decode_level11', 'decode_level10'];
        const decoded = inputs.map((json) => {
            const candidates = [];
            for (const name of decoderNames) {
                const decoder = runtime[name];
                if (typeof decoder !== 'function') {
                    continue;
                }

                try {
                    const bytes = Buffer.from(decoder(json));
                    if (bytes.length !== 16) {
                        continue;
                    }

                    const base64 = bytes.toString('base64');
                    if (!candidates.includes(base64)) {
                        candidates.push(base64);
                    }
                } catch {
                }
            }

            return candidates;
        });
        process.stdout.write(JSON.stringify(decoded));
        """;

    /// <summary>
    /// MSE(Media Source Extensions) 캡처 훅입니다.
    /// 플레이어가 디코더에 넘기는 바이트를 그대로 로컬 싱크로 복사합니다.
    /// 사이트가 세그먼트를 어떻게 암호화했든 이 지점에서는 이미 평문이므로 방식과 무관하게 동작합니다.
    /// 모든 프레임에 주입되며, 각 프레임이 자기 싱크로 직접 POST합니다.
    /// </summary>
    public static string BuildMediaSourceCaptureScript(int port, string token)
    {
        return CaptureTemplate
            .Replace("__WVD_PORT__", port.ToString(), StringComparison.Ordinal)
            .Replace("__WVD_TOKEN__", token, StringComparison.Ordinal);
    }

    private const string CaptureTemplate = """
        (() => {
            if (window.__wvdCaptureInstalled) {
                return;
            }

            window.__wvdCaptureInstalled = true;

            if (typeof MediaSource === 'undefined' || typeof SourceBuffer === 'undefined') {
                return;
            }

            const endpoint = 'http://127.0.0.1:__WVD_PORT__/chunk?token=__WVD_TOKEN__';
            const info = new WeakMap();
            const tracks = [];
            let nextTrackId = crypto.getRandomValues(new Uint32Array(1))[0] & 0x3fffffff;
            let captureError = null;
            window.__wvdFlushCapture = async () => {
                await Promise.all(tracks.map(track => track.queue));
                if (captureError) throw new Error(captureError);
                return true;
            };

            const send = (track, mime, seq, bytes) => {
                const url = endpoint + '&track=' + track + '&seq=' + seq + '&mime=' + encodeURIComponent(mime);
                return fetch(url, {
                    method: 'POST',
                    // text/plain은 CORS 안전 목록이라 별도 프리플라이트를 유발하지 않습니다.
                    headers: { 'Content-Type': 'text/plain;charset=UTF-8' },
                    body: bytes,
                    mode: 'cors',
                    credentials: 'omit',
                    cache: 'no-store',
                    referrerPolicy: 'no-referrer'
                }).then(response => {
                    if (!response.ok) throw new Error('캡처 전송 HTTP ' + response.status);
                }).catch(error => { captureError = String(error); });
            };

            const toBytes = (data) => {
                if (data instanceof ArrayBuffer) {
                    return new Uint8Array(data).slice();
                }

                if (ArrayBuffer.isView(data)) {
                    return new Uint8Array(data.buffer, data.byteOffset, data.byteLength).slice();
                }

                return null;
            };

            const originalAddSourceBuffer = MediaSource.prototype.addSourceBuffer;
            MediaSource.prototype.addSourceBuffer = function (mimeType) {
                const sourceBuffer = originalAddSourceBuffer.call(this, mimeType);

                try {
                    const track = {
                        id: nextTrackId++,
                        mime: String(mimeType || ''),
                        seq: 0,
                        // 전송을 직렬화해야 싱크에 도착하는 순서가 append 순서와 같아집니다.
                        queue: Promise.resolve()
                    };
                    info.set(sourceBuffer, track);
                    tracks.push(track);
                } catch {}

                return sourceBuffer;
            };

            const originalAppendBuffer = SourceBuffer.prototype.appendBuffer;
            SourceBuffer.prototype.appendBuffer = function (data) {
                const result = originalAppendBuffer.call(this, data);
                try {
                    const entry = info.get(this);
                    if (entry) {
                        const bytes = toBytes(data);
                        if (bytes && bytes.byteLength > 0) {
                            const seq = entry.seq++;
                            entry.queue = entry.queue.then(() => send(entry.id, entry.mime, seq, bytes));
                        }
                    }
                } catch {}

                return result;
            };
        })();
        """;
}

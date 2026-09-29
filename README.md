# WebVideoDownloader

웹페이지에서 실제 재생 가능한 영상 소스를 탐지하고, 우선순위 후보를 추천해 다운로드하는 Windows 데스크톱 앱입니다.

## 개요

이 프로젝트는 단순히 DOM의 `<video src>`만 보는 방식이 아니라, 브라우저 런타임·네트워크·응답 바디를 함께 분석해 영상 URL을 수집합니다.  
특히 일반 MP4뿐 아니라 HLS(m3u8), Level5 계열 플레이어(키 디코딩 필요)까지 대응하도록 설계되어 있습니다.

## 기능

- 페이지/네트워크/응답 바디/플레이어 스크립트를 종합한 영상 후보 탐지
- 후보 추천 정렬(형식, 화질 추정, 재생중 신호, 출처 기반 점수)
- 직접 파일 다운로드(MP4/WebM/MOV 등)
- HLS 다운로드(매니페스트 정규화 + ffmpeg/세그먼트 처리)
- 외부 HLS 요청이 401/403으로 거부되면 WebView2의 현재 페이지 세션에서 다시 요청
- ffmpeg 재시도 전에 HLS 리소스를 로컬로 수집하여 로컬 입력의 HTTP 옵션 오류 방지
- Level5 HLS 다운로드(WASM 런타임 기반 키 디코딩 후 세그먼트 복호화)
- 재생 캡처(MSE): 플레이어가 디코더에 넘기는 바이트를 그대로 저장해 사이트별 암호화와 무관하게 대응
- 다운로드 진행 상태/로그 표시, 취소, 저장 폴더 열기
- `data-video-url` 플레이어 설정 탐지 (`.shtml` HLS 주소 포함)
- 시작·종료 시간으로 구간 저장 (MP4)
- 다크/라이트 테마 토글

## 동작 원리

1. WebView2에 네트워크 프로브 스크립트를 주입합니다.
2. CDP(Network 이벤트), WebResource 이벤트, DOM 스캔 결과를 병합해 URL 후보를 수집합니다.
3. URL/Content-Type/재생 신호를 바탕으로 후보를 분류·점수화·정렬합니다.
4. 선택된 후보 타입에 따라 Direct / HLS / Level5 파이프라인으로 분기합니다.
5. 필요 시 키 추출·복호화·TS 검증 후 MP4로 remux합니다.

## 주요 코드 맵

| 영역 | 파일 | 역할 / 원리 |
|---|---|---|
| 앱 진입점 | [`Scripts/Program.cs`](./Scripts/Program.cs) | WinForms 앱 시작 및 메인 윈도우 실행 |
| 메인 UI/이벤트 | [`Scripts/MainWindow/MainWindow.cs`](./Scripts/MainWindow/MainWindow.cs) | 윈도우 초기화, 버튼 이벤트, 다운로드 실행 제어 |
| 웹 탐지 파이프라인 | [`Scripts/MainWindow/MainWindow.WebView.cs`](./Scripts/MainWindow/MainWindow.WebView.cs) | WebView2 네비게이션/스크립트 스캔/CDP 응답 분석/후보 초기 수집 |
| 후보 관리 | [`Scripts/MainWindow/MainWindow.Candidates.cs`](./Scripts/MainWindow/MainWindow.Candidates.cs) | 후보 추가·보강·재생중 표시·리스트 렌더링 |
| 다운로드 파이프라인 | [`Scripts/MainWindow/MainWindow.Downloads.cs`](./Scripts/MainWindow/MainWindow.Downloads.cs) | Direct/HLS/Level5 분기, 헤더·쿠키 처리, 세그먼트 다운로드 및 복호화 |
| 유틸/UI 상태 | [`Scripts/MainWindow/MainWindow.Utilities.cs`](./Scripts/MainWindow/MainWindow.Utilities.cs) | 상태/진행률/테마/로그/공용 유틸 함수 |
| 브라우저 주입 스크립트 | [`Scripts/Services/VideoProbeScripts.cs`](./Scripts/Services/VideoProbeScripts.cs) | fetch/XHR 가로채기, URL/HLS 탐지, Level5 디코더 JS 소스, MSE 캡처 훅 |
| 재생 캡처 싱크 | [`Scripts/Services/MediaCaptureSink.cs`](./Scripts/Services/MediaCaptureSink.cs) | 루프백 TCP 싱크로 `appendBuffer` 바이트 수집, 트랙별 파일 기록 |
| 후보 점수화 | [`Scripts/Services/CandidateDisplayService.cs`](./Scripts/Services/CandidateDisplayService.cs) | 화질 추정·우선순위 계산·추천 라벨 생성 |
| 미디어 분류 | [`Scripts/Services/MediaClassifier.cs`](./Scripts/Services/MediaClassifier.cs) | URL/Content-Type 기반 VideoKind 판별 |
| URL 추출 | [`Scripts/Services/MediaUrlExtractor.cs`](./Scripts/Services/MediaUrlExtractor.cs) | HTML/JS 텍스트에서 미디어/플레이어 URL 정규식 추출 |
| HLS 처리 | [`Scripts/Services/HlsManifestService.cs`](./Scripts/Services/HlsManifestService.cs) | 매니페스트 정규화, variant/key/segment 파싱 |
| 세그먼트 복호화 | [`Scripts/Services/TransportStreamService.cs`](./Scripts/Services/TransportStreamService.cs) | AES-128-CBC 후보 복호화 + MPEG-TS sync 기반 유효성 판정 |
| ffmpeg 실행기 | [`Scripts/Services/FfmpegRunner.cs`](./Scripts/Services/FfmpegRunner.cs) | ffmpeg 프로세스 실행, 진행률 파싱, 오류 수집 |
| 응답 바디 처리 | [`Scripts/Services/NetworkResponseReader.cs`](./Scripts/Services/NetworkResponseReader.cs) | aws-chunked 인코딩 대응 바이트 읽기 |
| URL 정규화 | [`Scripts/Services/UrlTools.cs`](./Scripts/Services/UrlTools.cs) | 상대/절대 URL 해석, 후보 URL 정규화 |
| 도메인 모델 | [`Scripts/Models/VideoCandidate.cs`](./Scripts/Models/VideoCandidate.cs) | 후보/표시정보/세그먼트/요청정보 레코드 정의 |

## 설계 포인트

- 단일 신호 의존 회피: DOM만으로 놓치는 케이스를 CDP·응답바디 분석으로 보완
- 단계별 폴백 전략: HLS/Level5 실패 시 다른 경로를 시도해 성공률 확보
- 후보 우선순위화: 사용자에게 “다운로드 가능한 가능성이 높은 항목”을 먼저 노출
- 최종 폴백은 재생 캡처: URL·키를 못 구해도 브라우저가 재생만 할 수 있으면 저장 가능

## 구간 저장 사용법

영상 후보를 선택한 뒤 **구간 저장**을 체크하고 시작·종료 시간을 입력하세요.
`90`, `01:30`, `00:01:30` 모두 1분 30초를 뜻합니다. 종료를 비워두면 끝까지 저장합니다.
현재는 전체 소스를 임시로 받은 뒤 지정 구간을 H.264/AAC MP4로 재인코딩합니다.
따라서 전체 다운로드에 필요한 시간과 임시 디스크 공간이 필요하며 ffmpeg가 설치되어 있어야 합니다.
재생 캡처 후보의 시간은 원본 페이지가 아닌 **모인 캡처 파일 시작점** 기준입니다.
시작 시간이 파일 길이를 넘으면 오류를 표시하고 빈 결과 파일을 삭제합니다.

## 재생 캡처 사용법

URL 탐지나 키 복호화가 통하지 않는 사이트를 위한 마지막 수단입니다.

1. 페이지를 열고 영상을 **재생**합니다.
2. 후보 목록에 `재생 캡처` 항목이 뜨고, 재생이 진행될수록 크기가 올라갑니다.
3. 원하는 만큼(보통 끝까지) 재생한 뒤 다운로드를 누르면 모아 둔 트랙을 하나로 합쳐 저장합니다.

저장할 때 전송 대기 중인 데이터를 기다린 다음 파일 사본을 만들며, 화질 전환으로 나뉜 조각도
트랙별 순서대로 모두 연결합니다. 후보의 **수신분만** 표시는 전체 원본 다운로드가 아니라는 뜻입니다.
전체 영상이 필요하면 **HLS 후보**를 선택하세요. 403 재시도 중에는 앱 안의 원본 페이지를 유지해야 합니다.

동작 원리상 **재생한 구간만** 저장됩니다. 또한 EME/Widevine 같은 DRM 콘텐츠는 브라우저 안에서도 평문이
JS로 노출되지 않으므로 캡처 대상이 아닙니다.

## 회귀 검증

`dotnet run --project Tests/Regression`으로 구간 입력과 플레이어 설정 분류를 검사합니다.
실제 ffmpeg 구간 저장까지 검사하려면 뒤에 `-- 입력.mp4 출력.mp4`를 붙이세요.
입력은 길이 5초인 테스트 영상, 출력은 덮어써도 되는 테스트 경로를 사용합니다.
세 번째 인수에 TS 테스트 파일을 전달하면 `.jpg` 세그먼트 주소를 로컬화한 HLS 저장도 검사합니다.
통합 검사는 `dotnet run --project Tests/BrowserRegression -- 페이지URL 리소스URL`로 실행합니다.
리소스는 외부 HTTP 요청에는 403을, 브라우저 요청에는 바이트 `01 02 03 04`를 반환하는 테스트 서버를 사용합니다.
페이지 URL만 주면 `data-video-url` HLS의 첫 세 조각을 실제 다운로드합니다.
`WVD_TEST_FULL=1` 환경 변수를 설정하면 전체 다운로드를 검사하며 결과는 임시 폴더의 `wvd-site-full.mp4`에 저장됩니다.

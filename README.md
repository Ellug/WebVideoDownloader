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
- Level5 HLS 다운로드(WASM 런타임 기반 키 디코딩 후 세그먼트 복호화)
- 다운로드 진행 상태/로그 표시, 취소, 저장 폴더 열기
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
| 브라우저 주입 스크립트 | [`Scripts/Services/VideoProbeScripts.cs`](./Scripts/Services/VideoProbeScripts.cs) | fetch/XHR 가로채기, URL/HLS 탐지, Level5 디코더 JS 소스 보관 |
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

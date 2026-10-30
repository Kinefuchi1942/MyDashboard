# MyDashboard プロジェクト概要

## 目的
自作の Windows アプリ。サブモニターに全画面で常時表示する「動的な壁紙」兼 PC モニター。
ゲーム中は CPU・GPU・FPS などの負荷を、普段は時計・天気・ニュースを見せる。見た目の良さも重視する。

## 依頼者と環境
- 依頼者は C# を書かない。成果物はそのままビルドできる形で渡し、手順は短く具体的に伝える
- PC：日本語 Windows、Intel Core i7-14700K（20スレッド）、NVIDIA GeForce RTX 系、サブモニターあり
- 開発：C# / .NET 8 / WPF。build.bat で単一 exe（publish フォルダ）を作る
- 配布：exe 1つだけ。`v1.2.3` のようなタグを push すると GitHub Actions（.github/workflows/release.yml）が make-dist.ps1 で dist\MyDashboard.exe を作り、Releases に公開する。説明書は同梱せず README に寄せる
- ソースは Program Files 以外に置く（Program Files の下ではビルドできない）

## 構成
- `MainWindow`：ウィンドウ本体。モニター配置、レイアウト切り替え、右クリックメニュー、キー操作
- `SettingsWindow`：設定画面（右クリック「設定…」または S キー）。左の一覧で 表示 / オーロラ / 警告の目安 / 消費電力 / ニュース / 天気 / 再生中の音楽 / Twitch / YouTube / FPS を切り替え。「保存」で settings.json に書き、MainWindow.ApplySettings が再起動なしで反映（変わった部分のサービスだけ作り直し、レイアウトは作り直す）。天気は Open-Meteo の地名検索（geocoding-api.open-meteo.com）で地点を選ぶ
- `Monitoring/`：センサー収集（1秒ごと）
  - HardwareMonitor：LibreHardwareMonitorLib 0.9.6（CPU / GPU / ストレージ）、メモリは GlobalMemoryStatusEx
  - CpuTopology：GetLogicalProcessorInformationEx の EfficiencyClass で P/E コアを判定し、センサー名「CPU Core #N」の N をコア順に当てる（取れなければ HT なしのコア＝E コアで推定）。CoreBars は E コアを淡色・グループ分け・ラベル付きで描く
  - FpsMonitor：Intel PresentMon（コンソール版）を子プロセスで起動し CSV を読む。診断を publish\fps-diagnostics.txt に出力
  - DlssDetector：ゲームが読み込んでいる nvngx_dlss / dlssg / dlssd の DLL（ドライバー差し替え時は ProgramData\NVIDIA\NGX\models 以下の .bin）から DLSS の SR・FG・RR を推定。診断を publish\dlss-diagnostics.txt に出力
  - NetworkMonitor：NetworkInterface の送受信量
  - SystemScanner：消費電力の見積もり用に PC の構成を調べる（起動時と設定画面の「構成を読み直す」）。マザーボード＝レジストリ HARDWARE\DESCRIPTION\System\BIOS、メモリ＝SMBIOS の Type 17（GetSystemFirmwareTable）、ドライブ＝\\.\PhysicalDriveN への IOCTL_STORAGE_QUERY_PROPERTY（BusType 17＝NVMe、シークペナルティあり＝HDD、USB・仮想は除外）、ファン＝HardwareMonitor の LHM で Motherboard を有効にして毎回数える（100 rpm 超。LHM の Computer を別に開いて閉じると、ドライバーが共有ごと閉じられて本体のセンサーが NullReference で読めなくなるので、別に開かない）、モニター＝QueryDisplayConfig（名前・リフレッシュレート・GDI 名）＋レジストリの EDID（大きさ）、明るさ＝DDC/CI（dxva2 GetMonitorBrightness、1分ごと・裏で）。診断を publish\power-diagnostics.txt に出力
  - PowerMeter：消費電力の推定（アプリ全体で1つ。設定の保存で Collector が作り直されても積算は続く）。CPU・GPU は LHM の実測、マザーボード＝Z/X 系 18 W・B/H/A 系 12 W・不明 15 W＋CPU 電力の 1 割（電源回路の損失）、メモリ＝DDR5 4 W・ほか 3 W／枚、ドライブ＝NVMe 1.5〜6 W・SATA SSD 0.8〜3 W・HDD 5〜8 W（LHM の読み書き速度で増やす。100 MB/s で最大）、ファン 1.5 W／個（読めなければ 4 個）、その他 5 W。合計を 80 PLUS の等級ごとの効率曲線（10・20・50・100% 負荷）で割り、補正を足す。モニター＝（インチ×1 W＋120Hz 以上 5 W／200Hz 以上 8 W）×（0.4＋0.6×明るさ）、明るさが読めなければ 50%、画面消灯中（MainWindow が GUID_CONSOLE_DISPLAY_STATE の通知で PowerMeter.DisplayOn を書く）は 0.5 W。電力量は publish\power-log.json（日・月ごと、1分ごとと終了時に保存。5 秒以上あいた間は数えない）
  - Collector：上記を1秒ごと（updateIntervalMs）にまとめて Snapshot にして画面へ渡す
- `Info/`：ニュースと天気（センサーとは別ループ）
  - InfoService：RSS を10分ごと、天気（Open-Meteo、API キー不要）を15分ごとに取得
  - FeedParser：RSS 2.0 / RSS 1.0 (RDF) / Atom を要素名で読む。配信元を交互に並べる
  - MediaService：Windows の再生中メディア情報（GlobalSystemMediaTransportControls）から Spotify などの曲名・ジャケット・再生位置を読む。通知方式で負荷ほぼゼロ。診断を publish\media-diagnostics.txt に出力。このため TargetFramework は net8.0-windows10.0.19041.0
- `Views/`：差し替え可能なレイアウト（IDashboardView：Render / UpdateClock / SetNotice / ShowNews / ShowWeather / ShowMedia）
  - ClassicView「標準」：情報量重視のパネル型（スレッド別負荷、ディスクごとの速度まで表示）。見出しの時計の左に消費電力
  - PowerSummary：消費電力（推定）の表示。合計 W・内訳の帯（CPU／GPU／その他／モニター）・今日と今月の kWh と電気代。標準と計器盤の時計の左に置く（2026-10-30 追加、モックアップ `mockups/power-classic.png`・`power-gauge.png`・`power-settings.png`）
  - GaugeView「計器盤」：CPU・FPS・GPU の円弧メーター3連。外側＝使用率、内側＝温度。FPS の満点はメインモニターのリフレッシュレート。CPU メーターの下にスレッド別負荷（CoreBars、P/E コア別・スレッド数と最大スレッドの %。2026-10-28 追加、モックアップ `mockups/gauge-threads.png`）。FPS メーターの下に直近10秒のフレームタイムのグラフ（FrameTimeChart）と 1% Low、左上に Present Mode のチップ（Hardware＝緑、Composed＝琥珀で「DWM 合成あり」）、右下に DLSS のチップ（SR / FG / RR。DlssDetector が読み込み済み DLL から推定。保護されたゲームは「unavailable」）。最下段にメモリ・VRAM・ディスク・ネットの細い帯。ニュースは表示しない（2026-10-26 に帯を削除）。Present Mode と DLSS は計器盤だけの表示（標準・オーロラには出ない）
  - AuroraView「オーロラ」：日常使い向け。大きな時計・天気（現在＋3時間ごと6コマ）・ニュースカードが主役。FPS は表示しない（2026-10-27 に左上のバッジを削除。ゲーム中の判定は Twitch の低画質切り替え用に残す）。PC 状態は下段の細いバー（x96・下端から84px・高さ120、CPU / GPU / メモリ / VRAM / ネットの5区画と推移グラフ）。背景の光が負荷に連動（CPU 青緑・GPU 紫・高温で赤）。
    - 左列（1920×1080 基準）：時計（y150・文字300）、日付（y470）、天気カード（x96・y556・幅860・高さ200）。曲の再生中は天気の下に再生中カード（x96・y726〜852・幅860・高さ126）を出し、そのあいだ時計・日付・天気を 50px 上げる（MediaLift）。背景にジャケット色の光。再生なし／一時停止5分でカードを隠す
    - 右列：メディアカード（ニュース⇄Twitch⇄YouTube）。右端から96px・上端 y48・幅844×高さ804で、天気・再生中カードの右24px、下段バーの手前まで。ニュースは見出し（46px）＋要約（空き高さに合わせて最大5行）＋次の記事4件、前へ／次へのボタンと件数表示。ダブルクリックで記事を開く
    - 「ニュース｜Twitch」で Twitch に手動で切り替え（AuroraView.Twitch.cs。起動時はニュース）。通常表示＝同じ位置・寸法のカードに映像 760×428（TwitchSlot）＋配信者名・視聴者数・経過時間・タイトル＋フォロー中で配信中の全員を丸いチップで並べる（入りきらない分は矢印／ホイールで横送り）。「拡張」で x96・y124・幅1728・高さ734 に広げ、映像（16:9・高さいっぱい）の右にチャット。拡張中は右上に時計と気温を出し、時計・天気・再生中カードは DailyLayer / MediaLayer ごと隠す
  - TwitchPlayer（Views/TwitchPlayer.cs）：WebView2 で Twitch 公式の埋め込みプレーヤーとチャットを出す。親ページは仮ドメイン https://mydashboard.local/（SetVirtualHostNameToFolderMapping。Twitch の parent 指定に必要）。WebView2 は HWND なので WPF の上に重ねられない・Viewbox の拡大は ZoomFactor で合わせる（1秒ごとにも確認）。映像は消音で始まる。チャットは拡張表示のときだけ iframe で読み込む。ゲーム中の低画質（設定でオン、360p 以上）は、ゲームが30秒続いてから下げ、終わって1分たってから戻す。WebView2 のデータ（ログインの Cookie）は publish\webview2。ニュースに戻すと Dispose
  - YouTube（AuroraView.YouTube.cs、2026-10-28 追加）：Twitch と同じカード・同じ拡張表示・同じ要素（TwitchSlot、TwitchName、TwitchChips など）を使い、AuroraView.Twitch.cs の _source（Twitch / YouTube）で見分ける。カードの「ニュース｜Twitch｜YouTube」のタブは選んでいる方にコードで色を付ける（Twitch 紫・YouTube 赤）。一覧は登録チャンネルの配信中（丸いチップ＋LIVE、視聴者の多い順）→新着動画（サムネイル付きの角丸チップ、新しい順に最大30本）。「＋ URL」はクリップボードの動画 URL を再生（文字入力欄を出さないため。WebView2 の上に入力欄を重ねられない）。表示中だけ1分ごとのタイマーで、配信中は3分・新着は15分たったものを取り直す（同時に4件まで）。動画が終わったら止める。埋め込み禁止（エラー 101/150）はメッセージを出し、選び直すまで再生し直さない。ゲーム中の低画質はなし（IFrame API で画質を変えられない）
  - WebPlayer（Views/WebPlayer.cs）：TwitchPlayer と YouTubePlayer の共通部分（WebView2 の初期化、仮ドメイン、ページへの命令の順番待ち、拡張表示の幅）。どちらのページも TwitchWeb.PageFolder が publish\webview2\page に書き出す
  - YouTubePlayer（Views/YouTubePlayer.cs）：YouTube IFrame Player API（youtube.html）。動画 ID 指定なら仮ドメインで再生できる（2026-10-28 に試作で確認）。チャンネル指定の配信（listType=live）はエラー 150 になるので使わない。拡張表示の右は、配信なら live_chat?v=…&embed_domain=mydashboard.local&dark_theme=1、動画ならページ内に新着の一覧（押すと pick:ID を送る）
  - YouTubeService（Info/YouTubeService.cs）：キー不要。/channel/ID/videos の ytInitialData（lockupViewModel）から新着（ショートと配信は入らない）、/channel/ID/live の ytInitialPlayerResponse（videoDetails.isLive）で配信中を判定。RSS（feeds/videos.xml）はチャンネルによって 404 になるので使わない。日本語のページ（Accept-Language: ja）の「8 時間前」から投稿時刻を逆算。配信の開始時刻は取れないことが多い（そのときは経過時間を出さない）
  - TwitchService（Info/TwitchService.cs）：Helix API（streams/followed、users）。Client ID は設定、トークンは publish\twitch-auth.json（Implicit grant。ログイン画面は TwitchLoginWindow が http://localhost への移動を横取り）
  - AuroraTheme：オーロラの時間帯テーマ（朝・昼は明るい背景＋濃い文字、夕方は紫と橙、夜はほぼ黒で約1割暗く）。境目は日の出／10時／日の入り1時間前／日の入り1時間後で、前後15分で背景を混ぜ、文字色は中間で3秒かけて切り替える。オーロラの文字・カードの色は DynamicResource（AText・AT85〜AT40・AGlass・ALine など）で、コードがブラシの色を変える。明るいテーマでは CPU #0B8494、GPU #5A52D0 の濃い版を使う
  - テーマ切替（AuroraView の ThemePicker）：メディアカードの右、画面右端に朝・昼・夕方・夜の四角を縦に並べる（中は各テーマの空の色を XAML に直書き）。クリックで settings.json の auroraTheme をそのテーマに固定して保存、固定中のものを再クリックで auto に戻す。いまの配色の四角を文字色の枠で囲む。モックアップは `mockups/aurora-theme-icons.png`
  - ViewSupport：共通の色・書式（Fmt）、ニュースの順送り（NewsRotator / NewsTicker）、演出（Fx）
- `Controls/`：自前描画の部品（HistoryChart、ArcGauge、FrameTimeChart、WeatherIcon、Meters.cs の BarMeter・CoreBars）
- `settings.json`：monitor、更新間隔、alwaysBehind、しきい値、layout（classic / gauge / aurora）、auroraMotion、auroraTheme（auto / morning / day / evening / night / classic）、auroraDayBrightness、enableMedia、mediaSpotifyOnly、enableTwitch / twitchClientId / twitchLowQualityInGame / twitchLastChannel、enableYouTube / youTubeChannels / youTubeLastVideo、memWarn / memCrit（メモリ・VRAM 使用率）、newsFeeds、天気の地点（緯度経度）、FPS 除外プロセス
- `app.ico`：アプリのアイコン（紺の角丸＋オーロラの光＋計器の弧。16〜40px は簡略版の絵）
- 操作：右クリックメニュー（レイアウト / 次のモニター / 常に背後 / 設定… / 設定ファイルを開く / 終了）、S＝設定画面、L＝レイアウト切替、M＝モニター移動、B＝常に背後（壁紙のように後ろのまま）、Ctrl+Q＝終了、ニュースはダブルクリックで記事を開く

## ファイル一覧

```
MyDashboard.csproj      .NET 8 / WPF。publish で単一 exe
app.manifest              管理者権限・高DPI対応
App.xaml                  配色・フォント・文字スタイル
MainWindow.xaml(.cs)      ウィンドウ本体。レイアウト切り替え、モニター配置、操作
SettingsWindow.xaml(.cs)  設定画面（settings.json を画面で編集し、保存ですぐ反映）
TwitchLoginWindow.cs      Twitch のログイン画面
AppSettings.cs            settings.json の読み書き（既定値はここで決める）
app.ico                   アプリのアイコン
Views/
  ClassicView             標準レイアウト
  PowerSummary            消費電力（推定）の表示。標準・計器盤で共通
  GaugeView               計器盤レイアウト
  AuroraView              オーロラレイアウト
  AuroraView.Twitch.cs    オーロラの Twitch 表示（通常／拡張、フォロー中の一覧、低画質の切り替え）
  AuroraView.YouTube.cs   オーロラの YouTube 表示（配信中・新着の一覧、＋URL、拡張表示の右側）
  WebPlayer.cs            Twitch・YouTube 共通の映像の部品（WebView2）
  TwitchPlayer.cs         Twitch の映像とチャット（WebView2）
  YouTubePlayer.cs        YouTube の映像とライブチャット・新着の一覧（WebView2）
  AuroraTheme.cs          オーロラの時間帯テーマ（朝・昼・夕方・夜の配色と切り替え）
  ViewSupport.cs          レイアウト共通の口（IDashboardView）・色・数値の書式
Info/
  InfoService.cs          ニュース（RSS）と天気（Open-Meteo）の取得
  FeedParser.cs           RSS 2.0 / RSS 1.0 / Atom の読み取り
  MediaService.cs         再生中の曲（Windows のメディア情報）
  TwitchService.cs        Twitch のフォロー中の配信（Helix API）とログイン情報
  YouTubeService.cs       YouTube のチャンネルの新着と配信中（公開ページから読む）
Controls/                 推移グラフ・円弧メーター・フレームタイムのグラフ・天気アイコン・棒グラフ（自前描画で軽量）
Monitoring/
  HardwareMonitor.cs      LibreHardwareMonitor で CPU / GPU / ディスクを取得
  NetworkMonitor.cs       送受信速度
  FpsMonitor.cs           PresentMon の出力から FPS・1% Low を計算
  CpuTopology.cs          P コア／E コアの判別
  DlssDetector.cs         DLSS（SR／FG／RR）の検出
  SystemScanner.cs        PC の構成（マザーボード・メモリ・ドライブ・ファン・モニター）を調べる
  PowerMeter.cs           消費電力の推定と、今日・今月の電力量の積算
  Snapshot.cs             1回分の測定値の入れ物
  Collector.cs            一定間隔でまとめて取得し、画面に渡す
build.bat                 本番ビルド（publish へ。PresentMon のダウンロードも聞く）
make-dist.ps1             配布用の単体 exe を dist\ に作る（PresentMon を vendor\ に用意して exe に埋め込む）
AutoStart.cs              自動起動（タスクスケジューラの「MyDashboard」）の登録・解除。右クリック「サインイン時に自動起動」
.github/workflows/release.yml  タグの push で exe を作って GitHub Releases に公開
mockups/                  デザインのモックアップ画像
docs/images/              README の画面写真（tools/Screenshots で作る）
tools/Screenshots/        README の画面写真を見本データから作るツール（本体の csproj からは DefaultItemExcludes で除外）
```

### README の画面写真を作り直す

見た目を変えたら、次の1行で `docs/images/` の4枚（`classic.png`・`gauge.png`・`aurora-night.png`・`aurora-day.png`、1920×1080）を作り直して commit する。

```
dotnet run -c Release --project tools/Screenshots
```

- 本体のレイアウト（Views）をそのまま使い、見本データ（ゲーム中を想定した負荷・架空のニュース・天気・曲）を流し込んで、画面の外のウィンドウで描いて保存する。センサー・ネット・管理者権限は使わず、動いているダッシュボードにも触らない（約25秒）
- 見本データは `tools/Screenshots/Program.cs`。Snapshot や Info のレコードの形を変えたら、ここも直さないとコンパイルが通らない
- オーロラは夜・昼のテーマに固定して撮る。WebView2（Twitch・YouTube）は描けないので、メディアカードはニュースのまま
- Windows でだけ動く（クラウドの Linux では作れない）

## これまでに分かった注意点（再発させないこと）
- .bat は Shift_JIS（CP932）・CRLF で保存し、chcp 65001 は使わない（UTF-8 だと行が壊れて誤実行される）
- PresentMon の CSV 列名は版によって大文字小文字が違う（msBetweenPresents）→ 大文字小文字を区別せずに照合する
- Twitch・YouTube の映像（msedgewebview2.exe）も PresentMon に描画として出るので、FpsMonitor で常に除外する（ゲームと間違えないため）
- LibreHardwareMonitorLib 0.9.6 では VRAM が SensorType.SmallData（MB単位）。開発版とは種別や単位が違うため、容量系のセンサーは名前で探して単位をそろえる
- CPU 温度の取得には PawnIO ドライバ（pawnio.eu）が必要。アプリは管理者権限で動く
- 管理者権限のアプリから記事を開くときは explorer.exe 経由にする（ブラウザを管理者権限で起動しないため）
- ゲームの邪魔をしない：フォーカスを奪わない（ShowActivated=False）、アニメーションは1秒20コマ程度に抑え（設定の animationFps。新しいアニメーションは必ず `Fx.Limit` を通す）、表示中のレイアウトでだけ動かす

## デザイン方針
- 暗い部屋で目に優しい暗色が基調。数字は Bahnschrift、日本語は Yu Gothic UI
- 資源ごとの色：CPU #5CC8D7、GPU #8E9CFF、メモリ #6FD3A8、ディスク #C9A7F0、ネット #7FB2F0
- 状態色：注意 #FFB547（琥珀）、危険 #FF5D5D（赤）。しきい値は settings.json で変更できる
- 各レイアウトは 1920×1080 で設計し、Viewbox でモニターに合わせて拡大縮小する
- 新しいデザインは、まずモックアップ画像を作って確認してもらってから実装する

## 今後の候補
- 今日の予定（Google カレンダーなどの非公開 iCal URL）
- カウントダウン（任意の日付まであと何日か）

## 進め方
- 変更後は XAML とコードの対応や構文を静的にチェックし、変更点と更新手順を短く伝える
- 実機で未確認の部分は正直に伝え、エラー画面・写真・fps-diagnostics.txt をもらって直す
- 更新手順：ダッシュボードを終了 → 新しいソースで build.bat → 起動（PresentMon.exe は publish にあればそれを、なければ exe に埋め込んだものを取り出して使う）

## ソース管理（git）
- `D:\MyDashboard` は git リポジトリ（ブランチ main）。GitHub の非公開リポジトリを正本にして、クラウドの Claude Code セッションでも作業する
- `publish/`・`bin/`・`obj/`・`dist/`・`old/` は `.gitignore` で除外（設定・Twitch のログイン・PresentMon を GitHub に上げない）
- `.gitattributes` で改行変換を止めている（.bat の Shift_JIS・CRLF を守るため）
- クラウド（Linux）ではコンパイル確認まで（csproj の `EnableWindowsTargeting`）。起動・画面・センサーの確認はこの PC で、取り込み（git pull）→ publish → 起動

### リリース（配布用の exe を公開する）

1. `MyDashboard.csproj` の `<Version>` を上げて commit・push する
2. 同じ版のタグを付けて push する（例：`git tag v1.0.1` → `git push origin v1.0.1`）
3. GitHub Actions の「リリース」が `make-dist.ps1` で exe を作り、Releases に `MyDashboard.exe` を載せる
   - タグと `<Version>` が違うと失敗する
   - 最新版はいつも `https://github.com/Kinefuchi1942/MyDashboard/releases/latest/download/MyDashboard.exe`

手元で exe だけ作るときは（動いているダッシュボードは止めなくて大丈夫です）：

```
powershell -NoProfile -ExecutionPolicy Bypass -File D:\MyDashboard\make-dist.ps1
```

- できるのは `dist\MyDashboard.exe` 1つだけ。.NET と PresentMon が入っていて、設定（settings.json）は初回起動時に既定値で作られる
- 自分の設定・Twitch のログイン（`twitch-auth.json`）・`webview2` フォルダ・診断ファイルは入りません
- 相手の PC に .NET を入れる必要はありません。CPU 温度を出すには相手側でも PawnIO が必要です
- 署名のない exe なので、初回は「Windows によって PC が保護されました」→「詳細情報」→「実行」が必要です（README に記載済み）

## 作り方（初回だけ）

1. **.NET 8 SDK を入れる**（入っていれば不要）
   PowerShell かコマンドプロンプトで：
   ```
   winget install Microsoft.DotNet.SDK.8
   ```
2. **ソースのフォルダを書き込みできる場所に置く**
   `C:\Program Files` や `D:\Program Files` の下はビルドできません。`D:\Dev\MyDashboard` のような場所に置いてください。
3. **`build.bat` をダブルクリック**
   `publish` フォルダに `MyDashboard.exe` ができます。途中で「PresentMon をダウンロードしますか」と聞かれたら **Y** を押すと FPS 表示が使えるようになります（make-dist.ps1 で作った exe なら PresentMon は内蔵済み）。
4. `publish` フォルダを好きな場所（例：`C:\Tools\MyDashboard`）に移して、`MyDashboard.exe` を起動します。

起動時に管理者権限の確認（UAC）が出ます。CPU 温度とクロックの読み取り、FPS の計測に必要なためです。

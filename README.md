# MyDashboard

ゲーム中の CPU・GPU・FPS・メモリ・VRAM・ディスク・ネットワークを、サブモニターに全画面で表示する Windows アプリです。
1920×1080 を基準に設計してあり、どの解像度のモニターでも縦横比を保って拡大縮小されます。

## レイアウト

3種類の見た目を切り替えられます。画面を右クリック →「レイアウト」から選ぶか、`L` キーで順番に切り替わります。選んだレイアウトは次回起動時も保持されます。

| レイアウト | 特徴 |
| --- | --- |
| 標準 | 情報量重視。CPU・GPU・FPS を大きく、スレッド別負荷やディスクごとの速度まで一覧できる（下の表を参照） |
| 計器盤 | 車の計器のような円弧メーター3連。中央が FPS（メインモニターのリフレッシュレートが満点）、左右が CPU と GPU。外側の弧が使用率、内側の細い弧が温度。CPU の下にスレッドごとの負荷（P-Core／E-Core 別の棒と、いちばん高いスレッドの %）。FPS の下に直近10秒のフレームタイムのグラフと 1% Low。左上に Present Mode（緑＝合成なし、琥珀＝DWM 合成あり）、右下に DLSS の検出（SR／FG／RR。ゲームが読み込んでいる DLL からの推定で、オン・オフ設定そのものは分からない）。最下段にメモリ・VRAM・ディスク・ネット。ニュースは表示しない |
| オーロラ | 日常使い向けの壁紙寄り。大きな時計・天気（今の天気と3時間ごとの予報）・右側の「メディアカード」（ニュース⇄Twitch⇄YouTube）が主役で、PC の状態は下段に控えめに表示。背景の光が負荷に連動して変化する（CPU＝左下の青緑、GPU＝右上の紫、高温になると右下が赤く）。FPS は表示しない（ゲーム中の FPS は標準・計器盤で見る） |

オーロラでは、Spotify で曲を流していると天気カードの下に「再生中」カードが出ます（そのあいだ時計・日付・天気が少し上に寄ります）（ジャケット・曲名・アーティスト・再生位置。前の曲／再生・一時停止／次の曲のボタン付き）。カードの後ろの光はジャケットの色になります。何も再生していないとき、または一時停止が5分続いたときはカードが消えます。メディアカード（右側）の大きさは、曲の再生にかかわらず一定です。Spotify へのログインや登録は不要です（Windows の「再生中のメディア」情報を読んでいます）。ブラウザの YouTube なども表示したい場合は `mediaSpotifyOnly` を `false` にします。うまく出ないときは `publish` フォルダの `media-diagnostics.txt` を確認してください。

オーロラの色は時間帯で変わります。朝（日の出〜10時）と昼（10時〜日の入りの1時間前）は明るい空色の画面、夕方（日の入りの前後1時間）は紫と橙、夜はほぼ黒で少し暗めになります。日の出・日の入りは天気と一緒に取得し、境目の30分でゆっくり移り変わります。昼の画面がまぶしいときは `auroraDayBrightness` を `0.8` などに下げてください。時間帯に関係なく色を固定したいときは `auroraTheme` を `morning` / `day` / `evening` / `night`、以前の暗い配色に戻すときは `classic` にします（各テーマの見た目の確認にも使えます）。画面の右端（ニュースカードの右）に並ぶ4つの四角いアイコンでも切り替えられます。押したテーマに固定され、固定中のアイコンをもう一度押すと時間帯の自動切り替えに戻ります（自動のときはアイコンの上に「自動」と出ます）。

オーロラの背景の動きを止めたい場合は `settings.json` の `auroraMotion` を `false` にしてください（光の強さの変化は残ります）。

## ニュースと天気

- **ニュース**：登録した配信元（RSS）から10分ごとに見出しを取り込み、8秒ごとに切り替えて表示します（オーロラのメディアカード）。見出しの下に記事の要約（空きに合わせて最大5行）と次の記事4件が並び、右下の矢印で前後に送れます。1つのサイトに偏らないよう、配信元を交互に並べます。見出しを**ダブルクリック**すると、その記事をブラウザで開きます。
- **天気**：[Open-Meteo](https://open-meteo.com/)（無料・登録不要）から15分ごとに取得します。地点は `settings.json` の緯度・経度で指定します。

### 配信元を変える

`settings.json` の `newsFeeds` に、名前と RSS の URL を追加・削除します。

```json
"newsFeeds": [
  { "name": "NHK", "url": "https://www.nhk.or.jp/rss/news/cat0.xml" },
  { "name": "4Gamer", "url": "https://www.4gamer.net/rss/index.xml" }
]
```

RSS 2.0・RSS 1.0（RDF）・Atom のどれでも読めます。サイトの RSS の URL は、多くの場合ページ下部の「RSS」リンクにあります。

### 天気の地点を変える

`weatherPlace`（表示名）、`weatherLatitude`（緯度）、`weatherLongitude`（経度）を書き換えます。緯度・経度は Google マップで地点を右クリックすると表示されます。

## Twitch（オーロラ）

オーロラのニュースカード右上の「ニュース｜Twitch」で、フォロー中の Twitch 配信に切り替えられます（手動で切り替え。起動したときはニュース）。

- 通常表示：ニュースカードの場所に映像と、フォロー中で配信中のチャンネル（全員。入りきらないときは矢印かマウスホイールで横に送ります）が並びます。押すとそのチャンネルに切り替わり、最後に見ていたチャンネルは次回も優先されます
- 拡張表示：右上の「拡張」で、時計・天気・再生中の曲の場所まで広げ、映像の右にチャットを出します（時計と気温は右上に小さく出ます）。「通常表示」で戻ります
- 映像は消音で始まります。音はプレーヤーのスピーカーのボタンで出します
- 設定「ゲーム中は低画質」をオンにすると、ゲームが30秒続いたあと 360p 程度に下げ、ゲームが終わって1分たつと戻します（初期値はオフ）
- ニュースに戻すと映像の部品（WebView2）ごと片付けるので、メモリも戻ります

### 最初に1回だけ：Client ID の登録とログイン

フォロー中の一覧は Twitch 公式の API で取るため、無料の「アプリ登録」が1回だけ必要です（Twitch アカウントの二段階認証を有効にしておく必要があります）。

1. https://dev.twitch.tv/console/apps/create を開き、Twitch アカウントでログイン
2. 次のとおり入れて「作成」
   - 名前：好きな名前（例：MyDashboard-（自分のID））。ほかの人と同じ名前は使えません
   - OAuth のリダイレクト URL：`http://localhost`
   - カテゴリー：Application Integration
   - クライアントのタイプ：**公開（Public）**
3. 一覧の「管理」を開き、「クライアント ID」をコピー
4. ダッシュボードの右クリック「設定…」→「Twitch」に Client ID を貼り付け、「ログイン」→ Twitch で「許可」
5. 「保存」

ログインは数十日で切れることがあります。切れたらカードに表示されるので、同じ画面で「ログイン」し直してください。ログイン情報は `publish\twitch-auth.json`、WebView2 のデータは `publish\webview2` フォルダに保存されます。

## YouTube（オーロラ）

オーロラのニュースカード右上の「ニュース｜Twitch｜YouTube」で、YouTube に切り替えられます（手動で切り替え。起動したときはニュース）。登録やキーは要りません。

1. 右クリック「設定…」→「YouTube」で、見たいチャンネルの URL（`youtube.com/@〇〇`）、@ハンドル、その人の動画の URL のどれかを入れて「＋ 追加」
2. 「保存」

- 一覧には、登録したチャンネルのうち配信中のもの（赤い LIVE 付き）が先頭に、そのあとに新着動画（新しい順。ショートは入りません）が並びます。押すとその動画に切り替わります
- カード右上の「＋ URL」：YouTube の動画の URL をコピーしてから押すと、その動画を再生します
- 拡張表示：配信なら映像の右にライブチャット、動画なら同じチャンネルの新着の一覧を出します（一覧を押すとその動画に切り替わります）
- 映像は消音で始まります。動画が終わったら止まります（次へは進みません）
- 配信中かどうかは3分ごと、新着は15分ごとに確かめます（YouTube を表示している間だけ）
- 投稿者が埋め込みを禁止している動画は、アプリの中では再生できません（カードにその旨が出ます）
- 新着と配信中は YouTube の公開ページから読んでいます。YouTube がページの作りを変えると一覧が出なくなることがあります
- ゲーム中の低画質は YouTube では使えません（埋め込みのプレーヤーは外から画質を変えられないため）

## 画面の見方（標準レイアウト）

| 場所 | 内容 |
| --- | --- |
| 上段左 | CPU 使用率（大きな数字）、温度、平均クロック、消費電力。背景の面グラフは直近90秒の推移 |
| 上段中 | GPU 使用率、温度、コアクロック、消費電力、ファン回転数 |
| 上段右 | FPS、1% Low、フレーム時間。計測中のゲーム名は画面左上にも出ます |
| 下段左 | スレッドごとの負荷（棒グラフ）。P コア／E コアがある CPU では左右に分け、E コアは淡い色で下に「P-Core」「E-Core」と表示 |
| 下段中 | メモリと VRAM の使用量・使用率・推移 |
| 下段右 | ネットワークの受信／送信速度、ディスクごとの読み込み／書き込み速度と使用率 |

数字は通常は白で、しきい値を超えると琥珀色（注意）、さらに超えると赤（危険）に変わります。しきい値は `settings.json` で変えられます。

## 作り方（初回だけ）

1. **.NET 8 SDK を入れる**（入っていれば不要）
   PowerShell かコマンドプロンプトで：
   ```
   winget install Microsoft.DotNet.SDK.8
   ```
2. **ソースのフォルダを書き込みできる場所に置く**
   `C:\Program Files` や `D:\Program Files` の下はビルドできません。`D:\Dev\MyDashboard` のような場所に置いてください。
3. **`build.bat` をダブルクリック**
   `publish` フォルダに `MyDashboard.exe` ができます。途中で「PresentMon をダウンロードしますか」と聞かれたら **Y** を押すと FPS 表示が使えるようになります。
4. `publish` フォルダを好きな場所（例：`C:\Tools\MyDashboard`）に移して、`MyDashboard.exe` を起動します。

起動時に管理者権限の確認（UAC）が出ます。CPU 温度とクロックの読み取り、FPS の計測に必要なためです。

### 友人に配る zip を作る

PowerShell で次を実行すると `dist\MyDashboard-<版>.zip` ができます（動いているダッシュボードは止めなくて大丈夫です）。

```
powershell -NoProfile -ExecutionPolicy Bypass -File D:\MyDashboard\make-dist.ps1
```

- 中身は `MyDashboard.exe`・`PresentMon.exe`・初期値の `settings.json`・自動起動の .bat・`はじめにお読みください.txt`（元は `dist-readme.txt`）
- 自分の設定・Twitch のログイン（`twitch-auth.json`）・`webview2` フォルダ・診断ファイルは入りません
- 相手の PC に .NET を入れる必要はありません。CPU 温度を出すには相手側でも PawnIO が必要です
- 署名のない exe なので、初回は「Windows によって PC が保護されました」→「詳細情報」→「実行」が必要です（説明書に記載済み）

## CPU 温度を表示するには（PawnIO）

CPU の温度・クロック・電力の読み取りには、LibreHardwareMonitor が使う **PawnIO** ドライバが必要です。
画面上部に「CPU温度を出すには PawnIO が必要です」と出ていたら、公式サイト <https://pawnio.eu/> からインストーラーを入れて、ダッシュボードを再起動してください。
（GPU・メモリ・ディスク・ネットワーク・FPS は PawnIO なしでも表示されます）

## FPS 表示について（PresentMon）

FPS は Intel の公式ツール **PresentMon（コンソール版）** を裏で動かして計測します。

- `build.bat` で Y を選べば自動で `publish\PresentMon.exe` に置かれます。
- 手動で置く場合は <https://github.com/GameTechDev/PresentMon/releases> から `PresentMon-x.y.z-x64.exe` をダウンロードし、`MyDashboard.exe` と同じフォルダ（または `tools` フォルダ）に置いてください。ファイル名は `PresentMon` で始まっていれば何でも構いません。
- 計測対象は「前面にあるウィンドウのゲーム」です。サブモニターのダッシュボードをクリックした直後は、描画フレーム数が一番多いアプリを自動で選びます。
- ブラウザや Discord などは `settings.json` の `fpsExcludeProcesses` で除外しています。誤ってそれらが表示されるときはここに追加してください。

## 操作

画面を右クリックするとメニューが出ます。キー操作はダッシュボードを一度クリックしてから使えます。

| 操作 | キー |
| --- | --- |
| 次のレイアウトへ切り替え | `L` |
| 次のモニターへ移動（選んだモニターは記憶されます） | `M` |
| 常に背後に表示の切り替え | `B` |
| 設定画面を開く | `S` |
| 終了 | `Ctrl + Q` |

**常に背後に表示**（右クリックメニュー・設定画面、または `B`）をオンにすると、ダッシュボードの上にブラウザなどを重ねたまま、ダッシュボードをクリックしたりニュースを操作したりしても、ブラウザは手前に表示されたままになります。この状態ではクリックしてもダッシュボードにキーが届かないため、切り替えは右クリックメニューか設定画面で行ってください。

ダッシュボードは起動時にフォーカスを奪わないので、ゲームをプレイ中に起動しても大丈夫です。
なお、フルスクリーン（排他モード）のゲーム中にダッシュボードをクリックすると、ゲームが最小化されることがあります。ゲームを「ボーダーレス ウィンドウ」に設定しておくと安心です。

## Windows 起動時に自動で開く

`publish` フォルダの `install-autostart.bat` を **右クリック →「管理者として実行」**。
サインインの約20秒後に、UAC の確認なしで起動するようになります。やめるときは `uninstall-autostart.bat` を同じように実行します。
旧名（GameDashboard）で登録していた場合も、`install-autostart.bat` をもう一度実行すれば古い登録を消して MyDashboard として登録し直します。

## settings.json

ふだんは **右クリックメニューの「設定…」（または `S` キー）** で開く設定画面から変更できます。「保存」を押すと、再起動しなくてもすぐ画面に反映されます。天気の地点は地名で検索して選べます。

ファイルを直接編集することもできます。exe と同じフォルダにあります（右クリックメニューの「設定ファイルを開く」でも開けます）。直接編集したときはアプリを再起動してください。

| 項目 | 既定値 | 意味 |
| --- | --- | --- |
| `monitor` | `-1` | 表示するモニター番号（0 始まり）。`-1` はメイン以外の最初のモニター |
| `updateIntervalMs` | `1000` | 更新間隔（ミリ秒、250〜5000） |
| `historySeconds` | `90` | グラフに残す秒数 |
| `alwaysBehind` | `false` | 常に背後に表示（壁紙のように、クリックしても他のウィンドウの後ろのまま） |
| `layout` | `classic` | レイアウト（`classic` 標準 / `gauge` 計器盤 / `aurora` オーロラ） |
| `auroraMotion` | `true` | オーロラの背景をゆっくり動かすか |
| `auroraTheme` | `auto` | オーロラの色テーマ（`auto` 時間帯で切り替え / `morning` / `day` / `evening` / `night` で固定 / `classic` 以前の暗い配色） |
| `auroraDayBrightness` | `1.0` | 朝・昼の明るいテーマの明るさ（0.5〜1.0。0.8 で2割暗く） |
| `enableNews` | `true` | ニュースを表示するか |
| `newsFeeds` | （5サイト） | ニュースの配信元（名前と RSS の URL） |
| `newsRefreshMinutes` | `10` | ニュースを取り込み直す間隔（分） |
| `newsRotateSeconds` | `8` | 見出しを切り替える間隔（秒） |
| `enableWeather` | `true` | 天気を表示するか |
| `weatherPlace` | `東京` | 天気の地点の表示名 |
| `weatherLatitude` / `weatherLongitude` | `35.6895` / `139.6917` | 天気の地点の緯度・経度 |
| `cpuTempWarn` / `cpuTempCrit` | `80` / `90` | CPU 温度の注意／危険（°C） |
| `gpuTempWarn` / `gpuTempCrit` | `75` / `85` | GPU 温度の注意／危険（°C） |
| `loadWarn` / `loadCrit` | `85` / `97` | 使用率の注意／危険（%） |
| `memWarn` / `memCrit` | `88` / `95` | メモリ・VRAM 使用率の注意／危険（%） |
| `enableMedia` | `true` | 再生中の曲を表示するか（オーロラ） |
| `mediaSpotifyOnly` | `true` | `true` は Spotify だけ、`false` はブラウザなど他のアプリの再生も表示 |
| `enableTwitch` | `true` | オーロラのニュースカードに Twitch への切り替えを出すか |
| `twitchClientId` | `""` | dev.twitch.tv で登録したアプリの Client ID（設定画面で入れる） |
| `twitchLowQualityInGame` | `false` | ゲーム中は Twitch を 360p 程度に下げる |
| `twitchLastChannel` | `""` | 最後に見ていたチャンネル（自動で保存。配信中なら次もこの人から） |
| `enableYouTube` | `true` | オーロラのニュースカードに YouTube への切り替えを出すか |
| `youTubeChannels` | `[]` | 登録した YouTube のチャンネル（`id` と `name`。設定画面で追加する） |
| `youTubeLastVideo` | `""` | 最後に見ていた動画（自動で保存。一覧にあれば次もこれから） |
| `enableFps` | `true` | FPS 計測のオン／オフ |
| `fpsExcludeProcesses` | （一覧） | FPS 計測から外すプロセス名 |

## 困ったとき

- **CPU 温度が「--」のまま** → PawnIO が入っているか確認。入れた後は再起動。
- **FPS が出ない** → `PresentMon.exe` が exe と同じフォルダにあるか確認。ゲームを前面にして数秒待つ。アンチチート付きのゲームでも、PresentMon は OS のイベントを読むだけなので通常は問題ありません。
- **ウイルス対策ソフトに止められる** → センサー読み取り用のドライバを使うため、まれに誤検知されます。`publish` フォルダを除外に追加してください。
- **別のモニターに出てしまう** → `M` キーか右クリックメニューで移動すると、その位置が記憶されます。

## 構成

```
MyDashboard.csproj      .NET 8 / WPF。publish で単一 exe
app.manifest              管理者権限・高DPI対応
App.xaml                  配色・フォント・文字スタイル
MainWindow.xaml(.cs)      ウィンドウ本体。レイアウト切り替え、モニター配置、操作
SettingsWindow.xaml(.cs)  設定画面（settings.json を画面で編集し、保存ですぐ反映）
Views/
  ClassicView             標準レイアウト
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
TwitchLoginWindow.cs      Twitch のログイン画面
AppSettings.cs            settings.json の読み書き
Controls/                 推移グラフ・円弧メーター・フレームタイムのグラフ・天気アイコン・棒グラフ（自前描画で軽量）
app.ico                   アプリのアイコン
Monitoring/
  HardwareMonitor.cs      LibreHardwareMonitor で CPU / GPU / ディスクを取得
  NetworkMonitor.cs       送受信速度
  FpsMonitor.cs           PresentMon の出力から FPS・1% Low を計算
  CpuTopology.cs          P コア／E コアの判別
  DlssDetector.cs         DLSS（SR／FG／RR）の検出
  Snapshot.cs             1回分の測定値の入れ物
  Collector.cs            一定間隔でまとめて取得し、画面に渡す
```

使用ライブラリ：[LibreHardwareMonitorLib](https://github.com/LibreHardwareMonitor/LibreHardwareMonitor)（MPL-2.0）、[PresentMon](https://github.com/GameTechDev/PresentMon)（MIT、別途ダウンロード）

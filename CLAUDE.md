# MyDashboard（Claude Code 向けメモ）

サブモニターに全画面で常時表示する自作 Windows アプリ。「動く壁紙」兼 PC モニター。
ゲーム中は CPU・GPU・FPS などの負荷を、普段は時計・天気・ニュース・Twitch を見せる。見た目の良さも重視する。

詳しい構成は `PROJECT.md`（開発者向け）、使い方と settings.json の項目は `README.md`（利用者向け）にある。
このファイルは「最初に知っておくこと」と「再発させない注意点」だけをまとめる。

## 依頼者について

- 依頼者（HIDENARI）は C# を書かない。コードの説明は不要で、結果と依頼者がやることだけを伝える
- 返答は日本語で、短く、具体的に。手順は番号付きで最小限にする
- 実機で確認できていない部分は正直に「未確認」と伝え、写真や診断ファイル（下記）をもらって直す
- 依頼者は画面の右側のニュース⇄Twitch⇄YouTube のカードを「メディアカード」と呼ぶ
- 新しいデザインや見た目の大きな変更は、先にモックアップ画像を作って確認してもらってから実装する
  （既存のモックアップは `mockups/`）。表示の削除や小さな位置調整ならモックアップは不要

## 環境

- PC：DESKTOP-OVLK9MJ、日本語 Windows、Core i7-14700K（20 スレッド）、NVIDIA GeForce RTX 系、サブモニターあり
- C# / .NET 8 / WPF。TargetFramework は `net8.0-windows10.0.19041.0`（再生中の曲を読む WinRT API のため。ビルドエラーが出たらまずここを疑う）
- 主なパッケージ：LibreHardwareMonitorLib 0.9.6、Microsoft.Web.WebView2 1.0.3856.49
- ソースの正本は `D:\MyDashboard`、実行版は `D:\MyDashboard\publish\MyDashboard.exe`
- アプリは管理者権限で動く（`app.manifest` で requireAdministrator）。CPU 温度（PawnIO）と PresentMon の FPS 計測に必要
- 自動起動はタスクスケジューラのタスク `MyDashboard`（ログオン時、最上位の特権、72 時間制限なし）
- claude.ai のプロジェクト側にも同じソースの写しがあり、クラウドの Claude が別に作業することがある。
  同じファイルを同時に触ると古い写しで上書きされる事故が過去にあったので、作業前に D: の中身が最新かを疑う
- 2026-10-29 から git 管理（main）。GitHub の非公開リポジトリを正本にして、クラウドの Claude Code セッションでも作業する。
  ローカルで作業を始める前に `git pull`、終わったら commit して push する
- クラウド（Linux）ではアプリを動かせない。できるのは編集と `dotnet build` によるコンパイル確認まで。
  ただしクラウドの .NET に WPF のビルド部品（Microsoft.NET.Sdk.WindowsDesktop）が入っていないと `dotnet build` は通らない。
  そのときは XAML の名前付き要素を仮の宣言で置き換えた別の csproj で C# だけコンパイル確認する（XAML は目で確認し、実機のビルドで確かめる）
  起動・画面・センサーの確認は依頼者の PC で行う（取り込み → publish → 起動は下の手順のとおり）

## ビルドと停止・起動（UAC なし）

ダッシュボードは管理者権限で動いているので、通常権限の Claude Code からは taskkill できない。
また exe を直接起動すると UAC が出る。次の手順なら依頼者の操作（Ctrl+Q や UAC の「はい」）は要らない。

1. 動いたままでもコンパイル確認はできる：`dotnet build MyDashboard.csproj -c Release`（出力は bin/ なので publish とぶつからない）
2. 停止：`publish\quit.request` という空ファイルを置く。アプリが1秒ごとに見ていて、自分で終了しファイルも消す
   - PowerShell：`New-Item -ItemType File D:\MyDashboard\publish\quit.request -Force`
   - 終わったかの確認：`Get-Process MyDashboard -ErrorAction SilentlyContinue` が何も返さなければ終了済み（通常1秒ほど）
3. 本番ビルド：`dotnet publish MyDashboard.csproj -c Release -o publish`
   - `build.bat` は最後に `choice` / `pause` / エクスプローラーを開くなど対話的なので、Claude Code からは dotnet を直接呼ぶ
   - `build.bat` がやっている残り：`install-autostart.bat` と `uninstall-autostart.bat` を publish へコピー（変えたときだけ必要）
4. 起動：`Start-ScheduledTask -TaskName MyDashboard`（または `schtasks /Run /TN MyDashboard`）。管理者権限のまま UAC なしで起動する
   - Git Bash から schtasks を呼ぶと `/Run` がパスに変換されるので、PowerShell を使う
5. 起動したか `Get-Process MyDashboard` で確かめる

- `publish\PresentMon.exe`（FPS 計測）、`publish\settings.json`（依頼者の設定）、`publish\twitch-auth.json`（Twitch のログイン）、`publish\webview2\`、`publish\power-log.json`（今日・今月の電力量）は消さない・上書きしない
- ソース側の `settings.json` は publish へ「新しければ上書き」でコピーされる。ソース側を編集すると依頼者の設定が初期値で上書きされるので、既定値を変えたいときは `AppSettings.cs` を直す
- 自動起動の登録（`install-autostart.bat`）は管理者として実行が必要で、Claude Code からはできない。必要なら依頼者に「右クリック →管理者として実行」を頼む
- 二重起動は Mutex で防いでいる。起動前に必ず前のプロセスが終わっていることを確かめる
- Program Files の下ではビルドできない（書き込み不可）

### バックアップ

- 変更前のバックアップは `D:\MyDashboard` の外（例 `D:\MyDashboard-old\<名前>`）に置く。
  中に置くと .cs が二重にコンパイルされてビルドが壊れる（SDK 形式の csproj はフォルダ以下の .cs をすべて拾う）
- `D:\MyDashboard\old\` には古い exe だけが入っている。ここに .cs を置かないこと

## 構成（要点だけ。詳しくは PROJECT.md）

- `App.xaml.cs`：二重起動防止、quit.request の監視
- `MainWindow`：モニター配置、レイアウト切替、右クリックメニュー、キー操作、常に背後（WndProc で制御）
- `SettingsWindow`：settings.json を画面で編集。保存で `MainWindow.ApplySettings` が再起動なしで反映
- `Monitoring/`：1秒ごとのセンサー収集。HardwareMonitor（LHM）、FpsMonitor（PresentMon の CSV、フレームタイム・1% Low・Present Mode）、DlssDetector、NetworkMonitor
- `Info/`：InfoService（RSS 10分・天気 Open-Meteo 15分）、FeedParser、MediaService（Windows SMTC で Spotify の再生中の曲）、TwitchService（Helix API）、YouTubeService（公開ページを読む。キー不要）
- `Views/`：差し替え可能なレイアウト（`IDashboardView`）
  - ClassicView「標準」、GaugeView「計器盤」、AuroraView「オーロラ」（時計・天気・再生中カード・メディアカード、時間帯テーマ `AuroraTheme.cs`、Twitch 部分は `AuroraView.Twitch.cs` と `TwitchPlayer.cs`、YouTube 部分は `AuroraView.YouTube.cs` と `YouTubePlayer.cs`。共通の WebView2 は `WebPlayer.cs`）
  - ViewSupport：共通の色・書式・ニュース送り・演出
- `Controls/`：自前描画の部品（ArcGauge、HistoryChart、FrameTimeChart、WeatherIcon、Meters）
- 操作：右クリック、S＝設定、L＝レイアウト、M＝モニター移動、B＝常に背後、Ctrl+Q＝終了
- 診断ファイル（publish 内）：`fps-diagnostics.txt`、`dlss-diagnostics.txt`、`media-diagnostics.txt`、`power-diagnostics.txt`
- 消費電力（標準・計器盤）：`Monitoring/PowerMeter.cs`（推定と積算）、`SystemScanner.cs`（構成の自動調査）、`Views/PowerSummary`（表示）

## 再発させない注意点

- **.bat は Shift_JIS（CP932）・CRLF で保存する。** `chcp 65001` は使わない（UTF-8 だと行が壊れて誤実行される）。編集後はエンコーディングと改行を必ず確かめる
- **PresentMon の CSV 列名は版によって大文字小文字が違う**（msBetweenPresents など）→ 区別せずに照合する。依頼者 PC は `--v1_metrics` で動いている
- **LibreHardwareMonitorLib 0.9.6 では VRAM が `SensorType.SmallData`（MB）。** 開発版とは種別や単位が違うので、容量系のセンサーは名前で探して単位をそろえる
- **CPU 温度には PawnIO ドライバ（pawnio.eu）が必要。** 管理者権限もやめられない
- **LibreHardwareMonitor の `Computer` は1つだけにする。** 別に開いて閉じると、ドライバーが共有ごと閉じられて本体のセンサーが「Object reference not set…」で読めなくなり、画面の数字が止まる（2026-10-30）
- **管理者権限のアプリから URL を開くときは `explorer.exe` 経由にする**（ブラウザを管理者権限で起動しないため）。常に背後モードでは先に `AllowSetForegroundWindow(ASFW_ANY)`
- **ゲームの邪魔をしない：** フォーカスを奪わない（ShowActivated=False）、アニメーションは1秒20コマ程度、表示中のレイアウトでだけ動かす
- **WPF の凍結ブラシ：** テーマで色が変わるブラシ（AText・AT85〜AT40・AGlass・ALine など）を Style / ControlTemplate / Trigger の Setter から DynamicResource で参照しない。WPF が Freeze し、色のアニメーションで「シールまたは固定されているため…」例外で落ちる。要素の属性に直接書くか、コードから設定する
- **WebView2 は WPF の上に重ねられない**（HwndHost なので常に最前面に描かれる）。Twitch は Canvas 上で枠の位置に置き、Viewbox の拡大率は ZoomFactor で合わせる。WebView2 の上に WPF の要素を出す設計にしない
- **Twitch・YouTube の映像（msedgewebview2.exe）は PresentMon に描画として出る**ので、FpsMonitor で常に除外する（ゲームと誤認するため）
- **YouTube の一覧は RSS を使わない**（チャンネルによって 404）。チャンネルのページの中の JSON を読むので、YouTube の作り替えで壊れたら YouTubeService を直す。チャンネル指定の配信埋め込み（listType=live）はエラー 150 になるので、配信は /live のページで動画 ID を調べてから流す
- Twitch の埋め込みは親ページを仮ドメイン `https://mydashboard.local/`（SetVirtualHostNameToFolderMapping）にして parent 指定を通している
- **DLSS：** NVIDIA ドライバーの差し替えが有効だと、ゲームは `nvngx_dlss.dll` でなく `C:\ProgramData\NVIDIA\NGX\models\dlss\…\*.bin` を読み込む。プリセットやフレーム生成の倍率は外からは分からない（依頼者には説明済み）
- **常に背後（alwaysBehind）モード中はクリックしてもキーが届かない。** 最前面（topmost）の機能は依頼者の希望で削除済み（2026-10-28）。戻さない
- 天気は Open-Meteo（キー不要）。地名検索は geocoding-api.open-meteo.com（language=ja）

## デザイン方針

- 暗い部屋で目に優しい暗色が基調（オーロラの朝・昼だけ明るい背景＋濃い文字）
- フォント：数字は Bahnschrift、日本語は Yu Gothic UI
- 資源ごとの色：CPU `#5CC8D7`、GPU `#8E9CFF`、メモリ `#6FD3A8`、ディスク `#C9A7F0`、ネット `#7FB2F0`
  - 明るいテーマでの濃い版：CPU `#0B8494`、GPU `#5A52D0`
- 状態色：注意 `#FFB547`（琥珀）、危険 `#FF5D5D`（赤）。しきい値は settings.json / 設定画面で変更できる
- 各レイアウトは 1920×1080 で設計し、Viewbox でモニターに合わせて拡大縮小する
- オーロラの配置（1920×1080 基準）：メディアカードは 844×804（上端 y48、天気・再生中カードの右 24px、Twitch 映像 760×428）。再生中カードは天気の下

## 作業の終わりに

- 変更したら `PROJECT.md` と `README.md` の該当箇所も直す
- ビルド → 起動まで済ませてから、依頼者に「何が変わったか」と「見てほしいところ」を短く伝える

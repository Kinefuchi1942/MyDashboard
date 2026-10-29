MyDashboard（マイダッシュボード）
==================================

サブモニターに全画面で表示しておく「動く壁紙」兼 PC モニターです。
ゲーム中は CPU・GPU・FPS などの負荷を、普段は時計・天気・ニュースを表示します。

■ 動く環境
  ・Windows 10（2004 以降）/ Windows 11、64 ビット
  ・.NET などの追加インストールは不要です（exe に入っています）

■ はじめかた
  1. zip を右クリック →「プロパティ」→ 下の「許可する」にチェック →「OK」
     （これをしないと、展開したあと警告が出たり動かないことがあります）
  2. zip を「すべて展開」して、好きな場所に置く
     ※「C:\Program Files」の下には置かないでください（設定を保存できません）
     　例：D:\MyDashboard や ドキュメントの中
  3. MyDashboard.exe をダブルクリック
     ・「Windows によって PC が保護されました」と出たら
       「詳細情報」→「実行」を押してください（個人が作ったアプリのため出ます）
     ・「このアプリがデバイスに変更を加えることを許可しますか？」は「はい」
       （CPU 温度と FPS を読むために管理者権限で動きます）

■ 操作
  ・右クリック ……… メニュー
  ・S ………………… 設定（天気の地域、ニュース、色が変わる温度など）
  ・L ………………… レイアウト切り替え（標準・計器盤・オーロラ）
  ・M ………………… 表示するモニターを切り替え
  ・B ………………… 常に背後（ほかのウィンドウの後ろに置く）
  ・Ctrl+Q …………… 終了
  ※「常に背後」の間はキーが効かないので、右クリックメニューを使ってください

■ CPU 温度を表示するには
  PawnIO というドライバが必要です。https://pawnio.eu/ からインストールして、
  MyDashboard を起動し直してください。
  （GPU・メモリ・ディスク・ネット・FPS はなくても表示されます）

■ ログオン時に自動で起動するには
  install-autostart.bat を右クリック →「管理者として実行」。
  やめるときは uninstall-autostart.bat を同じように実行します。
  ※ フォルダを移動したら、もう一度 install-autostart.bat を実行してください

■ Twitch を表示するには（任意）
  Twitch の開発者サイト（https://dev.twitch.tv/console）でアプリを登録して
  Client ID を取得し、設定画面（S）の Twitch 欄に入れてログインしてください。

■ アンインストール
  自動起動を登録していたら uninstall-autostart.bat を管理者として実行し、
  フォルダごと削除してください。ほかの場所には何も書き込みません。

■ 同梱しているもの
  ・PresentMon.exe（Intel、MIT ライセンス）… FPS の計測に使います
    https://github.com/GameTechDev/PresentMon
  ・LibreHardwareMonitorLib（MPL-2.0）… センサーの読み取り（exe に内蔵）
    https://github.com/LibreHardwareMonitor/LibreHardwareMonitor

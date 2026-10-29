@echo off
rem ログオン時に MyDashboard を管理者権限で自動起動する（UAC 確認なし）
net session >nul 2>&1
if errorlevel 1 (
  echo このファイルを右クリックして「管理者として実行」してください。
  pause
  exit /b 1
)
rem 旧名（GameDashboard）の登録が残っていれば消す
schtasks /Delete /TN "GameDashboard" /F >nul 2>&1
schtasks /Create /TN "MyDashboard" /TR "\"%~dp0MyDashboard.exe\"" /SC ONLOGON /RL HIGHEST /DELAY 0000:20 /F
if errorlevel 1 (
  echo 自動起動の登録に失敗しました。
  pause
  exit /b 1
)
rem 72時間で強制終了される既定の制限と、電源（バッテリー）による停止を外す
powershell -NoProfile -ExecutionPolicy Bypass -Command "Set-ScheduledTask -TaskName MyDashboard -Settings (New-ScheduledTaskSettingsSet -ExecutionTimeLimit ([TimeSpan]::Zero) -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries -MultipleInstances IgnoreNew) | Out-Null"
if errorlevel 1 (
  echo 自動起動の詳細設定に失敗しました。
) else (
  echo 自動起動を登録しました。次回のサインインから起動します。
  echo ※ exe の場所を移動したら、もう一度このファイルを実行してください。
)
pause

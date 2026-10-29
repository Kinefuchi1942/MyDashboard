@echo off
net session >nul 2>&1
if errorlevel 1 (
  echo このファイルを右クリックして「管理者として実行」してください。
  pause
  exit /b 1
)
schtasks /Delete /TN "GameDashboard" /F >nul 2>&1
schtasks /Delete /TN "MyDashboard" /F
pause

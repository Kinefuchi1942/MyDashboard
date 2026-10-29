@echo off
setlocal
cd /d "%~dp0"

echo ============================================
echo  MyDashboard ビルド
echo ============================================
echo.

rem ---- 書き込みできる場所か確認（Program Files などは不可）
echo test> "%~dp0.write_test" 2>nul
if not exist "%~dp0.write_test" goto nowrite
del "%~dp0.write_test" >nul 2>&1

rem ---- .NET SDK の確認
set HAS_SDK=
for /f "delims=" %%v in ('dotnet --list-sdks 2^>nul') do set HAS_SDK=1
if not defined HAS_SDK (
  echo .NET SDK が見つかりません。次のコマンドでインストールしてから、もう一度実行してください。
  echo.
  echo     winget install Microsoft.DotNet.SDK.8
  echo.
  echo （winget が使えない場合は https://dotnet.microsoft.com/download から .NET 8 SDK を入手）
  pause
  exit /b 1
)

rem ---- 1つの exe にまとめてビルド
echo exe を作成しています。初回は数分かかります...
dotnet publish MyDashboard.csproj -c Release -o publish
if errorlevel 1 (
  echo.
  echo ビルドに失敗しました。上のエラーメッセージを確認してください。
  pause
  exit /b 1
)

copy /y install-autostart.bat publish\ >nul
copy /y uninstall-autostart.bat publish\ >nul

rem ---- FPS 計測用の PresentMon（任意）
if exist publish\PresentMon.exe goto done
echo.
choice /c YN /m "FPS 表示に使う Intel PresentMon（公式 GitHub）をダウンロードしますか"
if errorlevel 2 goto done
powershell -NoProfile -ExecutionPolicy Bypass -Command ^
  "$ErrorActionPreference='Stop'; [Net.ServicePointManager]::SecurityProtocol='Tls12';" ^
  "$r = Invoke-RestMethod 'https://api.github.com/repos/GameTechDev/PresentMon/releases/latest' -Headers @{ 'User-Agent'='MyDashboard' };" ^
  "$a = $r.assets | Where-Object { $_.name -match '^PresentMon-[0-9.]+-x64\.exe$' } | Select-Object -First 1;" ^
  "if (-not $a) { throw 'PresentMon console exe not found in latest release' };" ^
  "Write-Host ('Downloading ' + $a.name);" ^
  "Invoke-WebRequest $a.browser_download_url -OutFile 'publish\PresentMon.exe' -UseBasicParsing"
if errorlevel 1 (
  echo PresentMon のダウンロードに失敗しました。README の手順で手動で配置できます。
)

:done
echo.
echo ============================================
echo  完成: publish\MyDashboard.exe
echo ============================================
echo publish フォルダごと好きな場所に移して使えます。
explorer publish
pause
exit /b 0

:nowrite
echo このフォルダには書き込みできないため、ビルドできません。
echo   %~dp0
echo.
echo Program Files の外（例: D:\Dev\MyDashboard）にフォルダごと移してから、
echo もう一度 build.bat を実行してください。
pause
exit /b 1

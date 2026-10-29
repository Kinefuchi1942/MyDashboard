# 友人に配る用の zip を作る（自分の設定やログイン情報は入れない）
# できるもの：dist\MyDashboard\（中身）と dist\MyDashboard-<版>.zip
$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot

$out = Join-Path $PSScriptRoot 'dist\MyDashboard'
if (Test-Path 'dist') { Remove-Item 'dist' -Recurse -Force }

# publish とは別の場所に出すので、動いているダッシュボードを止めなくてよい
dotnet publish MyDashboard.csproj -c Release -o $out
if ($LASTEXITCODE -ne 0) { throw 'ビルドに失敗しました' }

# WebView2 パッケージの説明用ファイルは要らない
Get-ChildItem $out -Filter '*.xml' | Remove-Item -Force

Copy-Item 'install-autostart.bat', 'uninstall-autostart.bat' $out
Copy-Item 'dist-readme.txt' (Join-Path $out 'はじめにお読みください.txt')

$pm = Get-ChildItem 'publish' -Filter 'PresentMon*.exe' -ErrorAction SilentlyContinue | Select-Object -First 1
if ($pm) { Copy-Item $pm.FullName (Join-Path $out 'PresentMon.exe') }
else { Write-Warning 'publish に PresentMon.exe がないので同梱しません（FPS 表示なし）' }

$ver = (Get-Item (Join-Path $out 'MyDashboard.exe')).VersionInfo.ProductVersion -replace '\+.*$', ''
$zip = Join-Path $PSScriptRoot "dist\MyDashboard-$ver.zip"
Compress-Archive -Path $out -DestinationPath $zip -Force
Write-Host "完成: $zip"

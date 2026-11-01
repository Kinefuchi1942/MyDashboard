# Velopack でインストーラーと更新パッケージを dist\releases に作る（vpk のバージョンは .config\dotnet-tools.json）
#   MyDashboard-win-Setup.exe   … 利用者に渡すインストーラー（.NET と PresentMon を内蔵。自分の設定やログイン情報は入れない）
#   MyDashboard-win-Portable.zip … インストールせずに使う版
#   *.nupkg, releases.win.json   … アプリ内の自動更新が読む
# 説明は README.md、バージョンは MyDashboard.csproj の <Version>
#   powershell -NoProfile -ExecutionPolicy Bypass -File make-dist.ps1
$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot

$dist = Join-Path $PSScriptRoot 'dist'
$app = Join-Path $dist 'app'
$releases = Join-Path $dist 'releases'
$work = Join-Path $PSScriptRoot 'obj\dist'
$vendor = Join-Path $PSScriptRoot 'vendor\PresentMon.exe'

# FPS 計測用の PresentMon を exe に埋め込むため vendor\ に用意する（publish にあればそれを使う）
if (-not (Test-Path $vendor)) {
    New-Item -ItemType Directory -Force (Split-Path $vendor) | Out-Null
    $local = Get-ChildItem 'publish' -Filter 'PresentMon*.exe' -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($local) {
        Copy-Item $local.FullName $vendor
    } else {
        [Net.ServicePointManager]::SecurityProtocol = 'Tls12'
        $headers = @{ 'User-Agent' = 'MyDashboard' }
        if ($env:GH_TOKEN) { $headers.Authorization = "Bearer $env:GH_TOKEN" }
        $r = Invoke-RestMethod 'https://api.github.com/repos/GameTechDev/PresentMon/releases/latest' -Headers $headers
        $a = $r.assets | Where-Object { $_.name -match '^PresentMon-[0-9.]+-x64\.exe$' } | Select-Object -First 1
        if (-not $a) { throw 'PresentMon の最新版にコンソール版の exe が見つかりません。' }
        Write-Host "PresentMon をダウンロード: $($a.name)"
        Invoke-WebRequest $a.browser_download_url -OutFile $vendor -UseBasicParsing
    }
}

# publish とは別の場所に出すので、動いているダッシュボードを止めなくてよい
foreach ($d in $dist, $work) { if (Test-Path $d) { Remove-Item $d -Recurse -Force } }
dotnet publish MyDashboard.csproj -c Release -o $work -nologo
if ($LASTEXITCODE -ne 0) { throw 'ビルドに失敗しました。' }

# 入れるのは exe だけ（WebView2 パッケージの説明用 xml などは要らない）
New-Item -ItemType Directory -Force $app | Out-Null
Copy-Item (Join-Path $work 'MyDashboard.exe') $app
Remove-Item $work -Recurse -Force

$version = ([xml](Get-Content 'MyDashboard.csproj' -Encoding UTF8)).Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1
if (-not $version) { throw 'MyDashboard.csproj に <Version> がありません。' }

dotnet tool restore
if ($LASTEXITCODE -ne 0) { throw 'vpk を用意できませんでした。' }

# packId はインストール先のフォルダ名（%LOCALAPPDATA%\MyDashboardApp）。
# %LOCALAPPDATA%\MyDashboard はアプリが WebView2 のデータと PresentMon に使っているので、別の名前にする
dotnet vpk pack --packId MyDashboardApp --packVersion $version --packTitle MyDashboard --packAuthors Kinefuchi1942 `
    --packDir $app --mainExe MyDashboard.exe --runtime win-x64 --icon (Join-Path $PSScriptRoot 'app.ico') -o $releases
if ($LASTEXITCODE -ne 0) { throw 'パッケージの作成に失敗しました。' }

# 利用者に見せるファイル名は MyDashboard-… にそろえる（更新パッケージの名前は packId のまま）
Get-ChildItem $releases -Filter 'MyDashboardApp-win-*' | ForEach-Object {
    Rename-Item $_.FullName ($_.Name -replace '^MyDashboardApp-', 'MyDashboard-')
}

Write-Host "作成しました: $releases"
Write-Host "配布用:       $(Join-Path $releases 'MyDashboard-win-Setup.exe')"

# 配布用の dist\MyDashboard.exe を作る（.NET と PresentMon を内蔵した単体の exe。自分の設定やログイン情報は入れない）
# 利用者はこの exe だけ置けば動く。説明は README.md、バージョンは MyDashboard.csproj の <Version>
#   powershell -NoProfile -ExecutionPolicy Bypass -File make-dist.ps1
$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot

$dist = Join-Path $PSScriptRoot 'dist'
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

# 配るのは exe だけ（WebView2 パッケージの説明用 xml などは要らない）
New-Item -ItemType Directory -Force $dist | Out-Null
Copy-Item (Join-Path $work 'MyDashboard.exe') $dist
Remove-Item $work -Recurse -Force

Write-Host "作成しました: $(Join-Path $dist 'MyDashboard.exe')"

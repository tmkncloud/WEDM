# Publishes WEDM self-contained x64 build and writes release-manifest.json with SHA-256 for each file under publish root.
param(
    [string] $Configuration = "Release",
    [string] $OutputRoot = ""
)

$ErrorActionPreference = "Stop"
$repoRoot = Split-Path -Parent $PSScriptRoot
$uiProject = Join-Path $repoRoot "src\WEDM.UI\WEDM.UI.csproj"
$propsRaw = Get-Content (Join-Path $repoRoot "Directory.Build.props") -Raw
if ($propsRaw -notmatch '<WedmProductVersion>([^<]+)</WedmProductVersion>') { throw "WedmProductVersion not found in Directory.Build.props" }
$ver = $Matches[1].Trim()
$ch = if ($propsRaw -match '<WedmReleaseChannel>([^<]+)</WedmReleaseChannel>') { $Matches[1].Trim() } else { "Stable" }

if (-not $OutputRoot) {
    $OutputRoot = Join-Path $repoRoot "artifacts\wedm-$ver-$ch-win-x64"
}

dotnet publish $uiProject -c $Configuration -r win-x64 --self-contained true /p:PublishSingleFile=false -o $OutputRoot

$artifacts = @()
Get-ChildItem -Path $OutputRoot -Recurse -File | ForEach-Object {
    $rel = $_.FullName.Substring($OutputRoot.Length).TrimStart('\')
    $hash = (Get-FileHash -Algorithm SHA256 -Path $_.FullName).Hash
    $artifacts += [ordered]@{
        name = $_.Name
        relativePath = ($rel -replace '\\','/')
        sha256 = $hash
    }
}

$manifest = [ordered]@{
    schemaVersion = 1
    productVersion = $ver
    releaseChannel = $ch
    publishedUtc = (Get-Date).ToUniversalTime().ToString("o")
    artifacts = $artifacts
}

$manifestPath = Join-Path $OutputRoot "release-manifest.json"
$manifest | ConvertTo-Json -Depth 8 | Set-Content -Path $manifestPath -Encoding UTF8
Write-Host "Release bundle written to $OutputRoot"
Write-Host "Manifest: $manifestPath"

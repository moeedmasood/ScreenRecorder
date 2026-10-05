<#
.SYNOPSIS  Builds a self-contained release, optionally signs it, and compiles the installer.
.EXAMPLE   .\installer\build.ps1
.NOTES     Signing: set SIGN_PFX (path) and SIGN_PFX_PASSWORD, or SIGN_CERT_THUMBPRINT, and make signtool.exe available (Windows SDK).
           Installer: needs Inno Setup 6 (iscc.exe on PATH or in Program Files). Without it, a portable ZIP is still produced.
#>
param([string]$Configuration = "Release")
$ErrorActionPreference = "Stop"
$root = Split-Path $PSScriptRoot -Parent
$out  = Join-Path $root "artifacts"
$pub  = Join-Path $out "publish"
$version = ([xml](Get-Content (Join-Path $root "Directory.Build.props"))).Project.PropertyGroup.Version
Remove-Item $out -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory $pub -Force | Out-Null

dotnet publish (Join-Path $root "src\ScreenRecorder.App\ScreenRecorder.App.csproj") -c $Configuration -r win-x64 --self-contained true `
    -p:PublishSingleFile=false -p:DebugType=none -o $pub
if ($LASTEXITCODE) { throw "publish failed" }

function Sign($file) {
    $signtool = Get-Command signtool.exe -ErrorAction SilentlyContinue | Select-Object -First 1 -ExpandProperty Source
    if (-not $signtool) { $signtool = Get-ChildItem "${env:ProgramFiles(x86)}\Windows Kits\10\bin\*\x64\signtool.exe" -ErrorAction SilentlyContinue | Sort-Object FullName -Descending | Select-Object -First 1 -ExpandProperty FullName }
    if (-not $signtool) { Write-Warning "signtool not found; skipping signing"; return }
    $args = @("sign", "/fd", "SHA256", "/tr", "http://timestamp.digicert.com", "/td", "SHA256")
    if ($env:SIGN_PFX) { $args += @("/f", $env:SIGN_PFX, "/p", $env:SIGN_PFX_PASSWORD) }
    elseif ($env:SIGN_CERT_THUMBPRINT) { $args += @("/sha1", $env:SIGN_CERT_THUMBPRINT) }
    else { Write-Warning "No signing certificate configured (SIGN_PFX / SIGN_CERT_THUMBPRINT); $file left unsigned"; return }
    & $signtool @args $file
    if ($LASTEXITCODE) { throw "signing failed for $file" }
}
Sign (Join-Path $pub "ScreenRecorder.App.exe")

Copy-Item (Join-Path $root "docs\*") $pub -Include "EULA.md","PRIVACY_POLICY.md" -ErrorAction SilentlyContinue
Copy-Item (Join-Path $root "THIRD_PARTY_NOTICES.md") $pub

Compress-Archive -Path "$pub\*" -DestinationPath (Join-Path $out "ScreenRecorder-$version-portable.zip")

$iscc = Get-Command iscc.exe -ErrorAction SilentlyContinue | Select-Object -First 1 -ExpandProperty Source
if (-not $iscc) { $iscc = @("${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe", "$env:ProgramFiles\Inno Setup 6\ISCC.exe", "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe") | Where-Object { Test-Path $_ } | Select-Object -First 1 }
if ($iscc) {
    & $iscc "/DAppVersion=$version" "/DSourceDir=$pub" "/DOutputDir=$out" (Join-Path $PSScriptRoot "ScreenRecorder.iss")
    if ($LASTEXITCODE) { throw "installer build failed" }
    Sign (Join-Path $out "ScreenRecorder-$version-setup.exe")
} else { Write-Warning "Inno Setup not found; installer skipped (portable ZIP created)." }

# Update manifest to upload next to the installer (fill in the real URL)
$setup = Join-Path $out "ScreenRecorder-$version-setup.exe"
if (Test-Path $setup) {
    $hash = (Get-FileHash $setup -Algorithm SHA256).Hash
    @{ version = $version; downloadUrl = "https://example.com/ScreenRecorder-$version-setup.exe"; notes = ""; sha256 = $hash } | ConvertTo-Json | Set-Content (Join-Path $out "update.json")
}
Get-ChildItem $out -File | Select-Object Name, @{n="MB";e={[math]::Round($_.Length/1MB,1)}}

param(
    [switch]$SkipPawnIoDownload
)

$ErrorActionPreference = "Stop"
$RootDir = Split-Path -Parent $PSScriptRoot
$Project = Join-Path $RootDir "src\LightRGB\LightRGB.csproj"
$PawnIoSetup = Join-Path $RootDir "src\LightRGB\pawnio\PawnIO_setup.exe"
$OutDir = Join-Path $RootDir "dist\LightRGB-Portable-win-x64"
$ZipPath = Join-Path $RootDir "dist\LightRGB-Portable-win-x64.zip"

if (-not $SkipPawnIoDownload -and -not (Test-Path $PawnIoSetup)) {
    Write-Host "Downloading official signed PawnIO_setup.exe..."
    Invoke-WebRequest -Uri "https://github.com/namazso/PawnIO.Setup/releases/latest/download/PawnIO_setup.exe" -OutFile $PawnIoSetup -UseBasicParsing
    $sig = Get-AuthenticodeSignature $PawnIoSetup
    if ($sig.Status -ne "Valid") {
        Remove-Item $PawnIoSetup -Force -ErrorAction SilentlyContinue
        throw "PawnIO_setup.exe signature check failed: $($sig.Status)"
    }
}

Write-Host "Publishing self-contained win-x64 portable build..."
dotnet restore $Project -r win-x64
dotnet publish $Project -c Release -r win-x64 --self-contained true --no-restore -p:DebugType=None -p:DebugSymbols=false -o $OutDir

Copy-Item (Join-Path $RootDir "README.md") (Join-Path $OutDir "README.md") -Force
Copy-Item (Join-Path $RootDir "LICENSE") (Join-Path $OutDir "LICENSE") -Force
Copy-Item (Join-Path $RootDir "THIRD_PARTY_NOTICES.md") (Join-Path $OutDir "THIRD_PARTY_NOTICES.md") -Force

$bad = Get-ChildItem $OutDir -Recurse -File | Where-Object {
    $_.Extension -eq ".pdb" -or
    $_.Name -eq "config.json" -or
    $_.Name -eq "Install_SignalRgb.exe" -or
    $_.FullName -match "\\plugins\\"
}
if ($bad) {
    throw "Unexpected files in portable folder: $($bad.FullName -join ', ')"
}

if (Test-Path $ZipPath) { Remove-Item $ZipPath -Force }
Compress-Archive -Path "$OutDir\*" -DestinationPath $ZipPath -CompressionLevel Optimal -Force
Write-Host "Created $ZipPath ($([Math]::Round((Get-Item $ZipPath).Length / 1MB, 1)) MB)"

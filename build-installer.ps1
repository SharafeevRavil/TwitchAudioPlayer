param(
    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Release"
)

$ErrorActionPreference = "Stop"

$root = $PSScriptRoot
$targetFramework = "net9.0-windows10.0.22621.0"
$publishDir = Join-Path $root "TwitchAudioPlayer.WPF\bin\$Configuration\$targetFramework\publish"
$wpfProject = Join-Path $root "TwitchAudioPlayer.WPF\TwitchAudioPlayer.WPF.csproj"
$setupProject = Join-Path $root "TwitchAudioPlayer.Setup\TwitchAudioPlayer.Setup.wixproj"

Write-Host "Publishing TwitchAudioPlayer.WPF ($Configuration)..." -ForegroundColor Cyan
if (Test-Path $publishDir) {
    Remove-Item -Recurse -Force -LiteralPath $publishDir
}
dotnet publish $wpfProject -c $Configuration -f $targetFramework -o $publishDir
if ($LASTEXITCODE -ne 0) {
    throw "Publish failed with exit code $LASTEXITCODE."
}

$msbuildCandidates = @(
    "${env:ProgramFiles}\Microsoft Visual Studio\18\BuildTools\MSBuild\Current\Bin\MSBuild.exe",
    "${env:ProgramFiles}\Microsoft Visual Studio\18\Community\MSBuild\Current\Bin\MSBuild.exe",
    "${env:ProgramFiles}\Microsoft Visual Studio\18\Professional\MSBuild\Current\Bin\MSBuild.exe",
    "${env:ProgramFiles}\Microsoft Visual Studio\18\Enterprise\MSBuild\Current\Bin\MSBuild.exe",
    "${env:ProgramFiles}\Microsoft Visual Studio\2022\BuildTools\MSBuild\Current\Bin\MSBuild.exe",
    "${env:ProgramFiles}\Microsoft Visual Studio\2022\Community\MSBuild\Current\Bin\MSBuild.exe",
    "${env:ProgramFiles}\Microsoft Visual Studio\2022\Professional\MSBuild\Current\Bin\MSBuild.exe",
    "${env:ProgramFiles}\Microsoft Visual Studio\2022\Enterprise\MSBuild\Current\Bin\MSBuild.exe",
    "${env:ProgramFiles(x86)}\Microsoft Visual Studio\2019\BuildTools\MSBuild\Current\Bin\amd64\MSBuild.exe"
)

$msbuild = $msbuildCandidates | Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $msbuild) {
    throw "MSBuild.exe was not found. Install Visual Studio Build Tools with WiX Toolset support."
}

$wixTargets = "${env:ProgramFiles(x86)}\MSBuild\Microsoft\WiX\v3.x\Wix.targets"
if (-not (Test-Path $wixTargets)) {
    throw "WiX Toolset v3.11 build targets were not found. Install WiX Toolset v3.11."
}

Write-Host "Building MSI with $msbuild..." -ForegroundColor Cyan
& $msbuild $setupProject `
    /t:Rebuild `
    /p:Configuration=$Configuration `
    /p:Platform=x86 `
    /p:BuildProjectReferences=false `
    /p:SkipAppProjectReference=true `
    /m
if ($LASTEXITCODE -ne 0) {
    throw "MSI build failed with exit code $LASTEXITCODE."
}

$msi = Join-Path $root "TwitchAudioPlayer.Setup\bin\$Configuration\TwitchAudioPlayer.msi"
if (Test-Path $msi) {
    Write-Host "MSI created: $msi" -ForegroundColor Green
} else {
    Write-Warning "Build finished, but MSI was not found at expected path: $msi"
}

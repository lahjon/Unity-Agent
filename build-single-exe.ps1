# Build Spritely as single executable
Write-Host "Building Spritely as single executable..." -ForegroundColor Green
Write-Host ""

# Clean previous builds
$publishDir = "bin\Release\net9.0-windows\win-x64\publish"
if (Test-Path $publishDir) {
    Write-Host "Cleaning previous build..." -ForegroundColor Yellow
    Remove-Item -Path $publishDir -Recurse -Force
}

# Build single file
Write-Host "Publishing as single file..." -ForegroundColor Yellow
dotnet publish -c Release -r win-x64 --self-contained true `
    -p:PublishSingleFile=true `
    -p:EnableCompressionInSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:PublishReadyToRun=true `
    -p:IncludeAllContentForSelfExtract=true

Write-Host ""
$exePath = Join-Path $publishDir "Spritely.exe"
if (Test-Path $exePath) {
    Write-Host "Build successful!" -ForegroundColor Green
    Write-Host "Single executable location: $exePath" -ForegroundColor Cyan

    $fileInfo = Get-Item $exePath
    $sizeInMB = [math]::Round($fileInfo.Length / 1MB, 2)
    Write-Host "File size: $sizeInMB MB" -ForegroundColor Cyan

    # Show only the exe file in publish directory
    Write-Host ""
    Write-Host "Published files:" -ForegroundColor Yellow
    Get-ChildItem $publishDir | Format-Table Name, @{Label="Size (MB)"; Expression={[math]::Round($_.Length / 1MB, 2)}}
} else {
    Write-Host "Build failed!" -ForegroundColor Red
}

Write-Host ""
Write-Host "Press any key to continue..."
$null = $Host.UI.RawUI.ReadKey("NoEcho,IncludeKeyDown")
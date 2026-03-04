@echo off
taskkill /IM Spritely.exe /F >nul 2>&1
echo Building Spritely...
dotnet publish Spritely.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true
if %ERRORLEVEL% NEQ 0 (
    echo Build FAILED.
    pause
    exit /b 1
)
echo.
echo Creating runtime folder...
if not exist "%~dp0runtime" mkdir "%~dp0runtime"
echo Copying executable to runtime folder...
copy /Y "%~dp0bin\Release\net9.0-windows\win-x64\publish\Spritely.exe" "%~dp0runtime\Spritely.exe"
if %ERRORLEVEL% NEQ 0 (
    echo Failed to copy executable to runtime folder.
    pause
    exit /b 1
)
echo.
if exist "%~dp0Spritely.lnk" (
    echo Updating existing shortcut...
) else (
    echo Creating shortcut...
)
powershell -NoProfile -Command "$ws = New-Object -ComObject WScript.Shell; $s = $ws.CreateShortcut('%~dp0Spritely.lnk'); $s.TargetPath = '%~dp0runtime\Spritely.exe'; $s.WorkingDirectory = '%~dp0'; $s.IconLocation = '%~dp0icon.ico'; $s.Save()"
echo Shortcut updated/created.
echo.
echo Build complete. Launching Spritely...
start "" "%~dp0runtime\Spritely.exe"
exit

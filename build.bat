@echo off
echo Building UnityAgent...
dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true
if %ERRORLEVEL% NEQ 0 (
    echo Build FAILED.
    pause
    exit /b 1
)
echo.
if exist "%~dp0UnityAgent.lnk" (
    echo Shortcut already exists, skipping.
) else (
    echo Creating shortcut...
    powershell -NoProfile -Command "$ws = New-Object -ComObject WScript.Shell; $s = $ws.CreateShortcut('%~dp0UnityAgent.lnk'); $s.TargetPath = '%~dp0bin\Release\net9.0-windows\win-x64\publish\UnityAgent.exe'; $s.WorkingDirectory = '%~dp0'; $s.IconLocation = '%~dp0icon.ico'; $s.Save()"
    echo Shortcut created.
)
echo.
echo Build complete. Launching UnityAgent...
start "" "%~dp0bin\Release\net9.0-windows\win-x64\publish\UnityAgent.exe"
exit

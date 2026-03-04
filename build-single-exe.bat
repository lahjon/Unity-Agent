@echo off
echo Building Spritely as single executable...
echo.

REM Clean previous builds
if exist "bin\Release\net9.0-windows\win-x64\publish\" rd /s /q "bin\Release\net9.0-windows\win-x64\publish\"

REM Publish as single file
dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:EnableCompressionInSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:PublishReadyToRun=true

echo.
if exist "bin\Release\net9.0-windows\win-x64\publish\Spritely.exe" (
    echo Build successful!
    echo Single executable location: bin\Release\net9.0-windows\win-x64\publish\Spritely.exe
    echo File size:
    dir "bin\Release\net9.0-windows\win-x64\publish\Spritely.exe" | findstr "Spritely.exe"
) else (
    echo Build failed!
)

pause
@echo off
echo Building Happy Engine as single executable...
echo.

REM Clean previous builds
if exist "bin\Release\net9.0-windows\win-x64\publish\" rd /s /q "bin\Release\net9.0-windows\win-x64\publish\"

REM Publish as single file
dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:EnableCompressionInSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:PublishReadyToRun=true

echo.
if exist "bin\Release\net9.0-windows\win-x64\publish\HappyEngine.exe" (
    echo Build successful!
    echo Single executable location: bin\Release\net9.0-windows\win-x64\publish\HappyEngine.exe
    echo File size:
    dir "bin\Release\net9.0-windows\win-x64\publish\HappyEngine.exe" | findstr "HappyEngine.exe"
) else (
    echo Build failed!
)

pause
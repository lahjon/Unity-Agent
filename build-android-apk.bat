@echo off
REM ──────────────────────────────────────────────────────────
REM  Build SpritelyRemote Android APK
REM  Requirements: Android SDK, Java 17+, ANDROID_HOME set
REM ──────────────────────────────────────────────────────────

echo.
echo  Building SpritelyRemote APK...
echo.

pushd "%~dp0SpritelyRemote"

REM Use gradlew if available, otherwise fall back to gradle
if exist "gradlew.bat" (
    call gradlew.bat assembleDebug
) else (
    call gradle assembleDebug
)

if %ERRORLEVEL% NEQ 0 (
    echo.
    echo  BUILD FAILED
    popd
    exit /b 1
)

echo.
echo  Build successful!
echo  APK location: SpritelyRemote\app\build\outputs\apk\debug\app-debug.apk
echo.

REM Copy APK to root for easy access
if exist "app\build\outputs\apk\debug\app-debug.apk" (
    copy "app\build\outputs\apk\debug\app-debug.apk" "%~dp0SpritelyRemote.apk" >nul
    echo  Copied to: SpritelyRemote.apk
)

popd

@echo off
setlocal
cd /d "%~dp0"

echo ==============================================
echo Desktop Calendar Widget - Bilingual Release
echo Simplified Chinese / English
    echo ==============================================
echo.

dotnet restore DesktopCalendarWidget.csproj
if errorlevel 1 (
    echo.
    echo Restore failed. Please make sure .NET 8 SDK is installed.
    pause
    exit /b 1
)

dotnet publish DesktopCalendarWidget.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o publish
if errorlevel 1 (
    echo.
    echo Publish failed.
    pause
    exit /b 1
)

echo.
echo Build complete.
echo Output: %cd%\publish
pause
endlocal

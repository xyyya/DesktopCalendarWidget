@echo off
setlocal
cd /d "%~dp0"
echo Starting Desktop Calendar Widget...
where dotnet >nul 2>&1
if errorlevel 1 (
  echo.
  echo ERROR: .NET SDK/runtime command "dotnet" was not found.
  echo Please install the .NET 8 SDK with Windows Desktop Runtime support.
  pause
  exit /b 1
)
dotnet run --project "DesktopCalendarWidget.csproj"
if errorlevel 1 (
  echo.
  echo The application exited with an error. The message above is the real build/runtime error.
  pause
)

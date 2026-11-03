@echo off
setlocal
rem Builds the app folder: publish\VoiceNoteTranscriber
set OUT=publish\VoiceNoteTranscriber
cd /d "%~dp0"

where dotnet >nul 2>nul
if errorlevel 1 (
    echo The .NET 10 SDK is required: https://dotnet.microsoft.com/download
    pause
    exit /b 1
)

echo Building...
if exist "%OUT%" rmdir /s /q "%OUT%"
dotnet publish src\VoiceNoteTranscriber -c Release -o "%OUT%" --nologo
if errorlevel 1 (
    echo Build failed.
    pause
    exit /b 1
)

echo Done! Run %OUT%\VoiceNoteTranscriber.exe
pause

@echo off
setlocal
rem Builds the app folder and the release zip: publish\IHateVoiceNotes_v<VERSION>.zip
set VERSION=1.0.0
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
dotnet publish src\VoiceNoteTranscriber -c Release -o "%OUT%" -p:Version=%VERSION% --nologo
if errorlevel 1 (
    echo Build failed.
    pause
    exit /b 1
)

rem GPL-3.0: the license goes with the app.
copy /y LICENSE "%OUT%\LICENSE.txt" >nul

echo Zipping release...
powershell -NoProfile -Command "$ErrorActionPreference='Stop'; Compress-Archive -Path '%OUT%' -DestinationPath 'publish\IHateVoiceNotes_v%VERSION%.zip' -Force"

echo Done! App folder: %OUT%\  Release zip: publish\IHateVoiceNotes_v%VERSION%.zip
pause

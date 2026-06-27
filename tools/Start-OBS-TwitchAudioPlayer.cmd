@echo off
setlocal

set "OBS_DIR=C:\Program Files\obs-studio\bin\64bit"
set "OBS_EXE=%OBS_DIR%\obs64.exe"
set "CEF_PROFILE=%LOCALAPPDATA%\TwitchAudioPlayer\ObsCefUnsafeProfile"

if not exist "%OBS_EXE%" (
    echo OBS was not found at "%OBS_EXE%".
    exit /b 1
)

if not exist "%CEF_PROFILE%" mkdir "%CEF_PROFILE%"

cd /d "%OBS_DIR%"
start "" "%OBS_EXE%" --disable-web-security --user-data-dir="%CEF_PROFILE%"

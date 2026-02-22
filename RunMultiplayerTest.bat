@echo off
set HOST_EXE="TinyCombatArena[Host]\Arena.exe"
set CLIENT_EXE="TinyCombatArena[Client]\Arena.exe"
set LOG_DIR=BepInEx\TCAMultiplayer

echo ==========================================
echo TCA Multiplayer - Test Session Launcher
echo ==========================================
echo.

:: Clean up old TCA log files
if exist "%LOG_DIR%" (
    echo Cleaning up old logs...
    del /q "%LOG_DIR%\*.log" 2>nul
)

:: Echo session timestamp
echo Session started at: %date% %time%
echo.

:: Launch first instance (will become HOST)
echo Launching first instance (HOST)...
start "" %HOST_EXE%

:: Launch second instance (will become CLIENT)
echo Launching second instance (CLIENT)...
start "" %CLIENT_EXE%

echo.
echo ==========================================
echo Both instances launched!
echo Host instance started first
echo Wait a few seconds, then:
echo   1. Press F8 in first instance to open menu
echo   2. Click Host to create a lobby
echo   3. Press F8 in second instance
echo   4. Click Join to connect to host
echo ==========================================
echo.
echo Logs will be in: %LOG_DIR%
echo.
pause

@echo off
setlocal enabledelayedexpansion

set HOST_EXE="TinyCombatArena[Host]\Arena.exe"
set CLIENT_EXE="TinyCombatArena[Client]\Arena.exe"
set LOG_DIR=BepInEx\TCAMultiplayer

set PROJECT_DIR=src
set OUTPUT_DIR=src\bin\Release\net472
set PLUGIN_NAME=TCAMultiplayer.dll
set DEPLOY_DIR_HOST=TinyCombatArena[Host]\BepInEx\plugins
set DEPLOY_DIR_CLIENT=TinyCombatArena[Client]\BepInEx\plugins

echo ==========================================
echo TCA Multiplayer - Test Session Launcher
echo ==========================================
echo.

echo [1/4] Building TCAMultiplayer in Release mode...
echo.

dotnet build "%PROJECT_DIR%\TCAMultiplayer.csproj" -c Release

if %ERRORLEVEL% neq 0 (
    echo.
    echo [ERROR] Build failed! Error code: %ERRORLEVEL%
    pause
    exit /b %ERRORLEVEL%
)

echo.
echo [2/4] Deploying %PLUGIN_NAME% to Host instance...
echo.

if not exist "%OUTPUT_DIR%\%PLUGIN_NAME%" (
    echo [ERROR] Built assembly not found at: %OUTPUT_DIR%\%PLUGIN_NAME%
    pause
    exit /b 1
)

copy /Y "%OUTPUT_DIR%\%PLUGIN_NAME%" "%DEPLOY_DIR_HOST%\%PLUGIN_NAME%"

if %ERRORLEVEL% neq 0 (
    echo [ERROR] Deployment to Host failed! Error code: %ERRORLEVEL%
    pause
    exit /b %ERRORLEVEL%
)

echo.
echo [3/4] Deploying %PLUGIN_NAME% to Client instance...
echo.

copy /Y "%OUTPUT_DIR%\%PLUGIN_NAME%" "%DEPLOY_DIR_CLIENT%\%PLUGIN_NAME%"

if %ERRORLEVEL% neq 0 (
    echo [ERROR] Deployment to Client failed! Error code: %ERRORLEVEL%
    pause
    exit /b %ERRORLEVEL%
)

echo.
echo Deploy complete: %DEPLOY_DIR_HOST%\%PLUGIN_NAME%
echo Deploy complete: %DEPLOY_DIR_CLIENT%\%PLUGIN_NAME%
echo.

:: [4/4] Launch test session
echo [4/4] Launching test session...
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
echo   1. Click MULTIPLAYER in the main menu of the first instance
echo   2. Click HOST GAME, then START SERVER
echo   3. Click MULTIPLAYER in the second instance
echo   4. Click JOIN GAME to connect to the host
echo ==========================================
echo.
echo Logs will be in: %LOG_DIR%
echo.
pause

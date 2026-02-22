@echo off
setlocal enabledelayedexpansion

echo ============================================
echo TCAMultiplayer Build and Deploy Script
echo ============================================
echo.

set PROJECT_DIR=src
set OUTPUT_DIR=src\bin\Release\net472
set PLUGIN_NAME=TCAMultiplayer.dll
set DEPLOY_DIR_HOST=TinyCombatArena[Host]\BepInEx\plugins
set DEPLOY_DIR_CLIENT=TinyCombatArena[Client]\BepInEx\plugins

echo [1/3] Building TCAMultiplayer in Release mode...
echo.

dotnet build "%PROJECT_DIR%\TCAMultiplayer.csproj" -c Release

if %ERRORLEVEL% neq 0 (
    echo.
    echo [ERROR] Build failed! Error code: %ERRORLEVEL%
    pause
    exit /b %ERRORLEVEL%
)

echo.
echo [2/3] Deploying %PLUGIN_NAME% to Host instance...
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
echo [3/3] Deploying %PLUGIN_NAME% to Client instance...
echo.

copy /Y "%OUTPUT_DIR%\%PLUGIN_NAME%" "%DEPLOY_DIR_CLIENT%\%PLUGIN_NAME%"

if %ERRORLEVEL% neq 0 (
    echo [ERROR] Deployment to Client failed! Error code: %ERRORLEVEL%
    pause
    exit /b %ERRORLEVEL%
)

echo.
echo ============================================
echo Build and Deploy completed successfully!
echo Output (Host):   %DEPLOY_DIR_HOST%\%PLUGIN_NAME%
echo Output (Client): %DEPLOY_DIR_CLIENT%\%PLUGIN_NAME%
echo ============================================
echo.

pause

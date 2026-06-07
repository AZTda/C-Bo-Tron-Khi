@echo off
title MOS Gas Sensor Control Software (C# WPF)
echo Starting C# WPF Gas Sensor Control Software...
echo.
cd /d "%~dp0src"
dotnet run
if %errorlevel% neq 0 (
    echo.
    echo [ERROR] Application exited with error code %errorlevel%.
    echo Please make sure .NET SDK is installed.
    echo.
    pause
)

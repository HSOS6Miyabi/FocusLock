@echo off
setlocal EnableExtensions
cd /d "%~dp0"

set "CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
if not exist "%CSC%" set "CSC=%WINDIR%\Microsoft.NET\Framework\v4.0.30319\csc.exe"

if not exist "%CSC%" (
    echo The .NET Framework 4 C# compiler is unavailable on this Windows installation.
    echo Please install/enable .NET Framework 4.x and try again.
    pause
    exit /b 1
)

if not exist "%~dp0GameFocusGuard.exe" (
    echo Building GameFocusGuard.exe locally for the first run...
    "%CSC%" /nologo /target:winexe /optimize+ /codepage:65001 /out:"%~dp0GameFocusGuard.exe" /reference:System.Windows.Forms.dll /reference:System.Drawing.dll /reference:System.dll "%~dp0GameFocusGuard.cs"
    if errorlevel 1 (
        echo Build failed. Copy the compiler error and send it back for a fix.
        pause
        exit /b 1
    )
)

start "" "%~dp0GameFocusGuard.exe"

@echo off
rem Builds OledGuard.exe with the .NET Framework 4.x compiler that ships with Windows.
rem Pass 1 builds a throwaway exe that draws the cat icon; pass 2 embeds it.
rem Stops a running instance first (it locks the exe); restart: schtasks /run /tn OledGuard
cd /d "%~dp0"
set CSC="%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe" /nologo /target:winexe /optimize+ /platform:x64 /r:System.Windows.Forms.dll /r:System.Drawing.dll /r:System.Management.dll
%CSC% /out:"%TEMP%\OledGuard-icon.exe" OledGuard.cs || exit /b 1
"%TEMP%\OledGuard-icon.exe" --make-icon OledGuard.ico || exit /b 1
del "%TEMP%\OledGuard-icon.exe"
if exist OledGuard.exe "%~dp0OledGuard.exe" exit && ping -n 2 127.0.0.1 >nul
%CSC% /win32icon:OledGuard.ico /out:OledGuard.exe OledGuard.cs

@echo off
setlocal
set CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe
if not exist "%~dp0bin" mkdir "%~dp0bin"
"%CSC%" /nologo /codepage:65001 /optimize+ /target:winexe /out:"%~dp0bin\zmk-hub-display.exe" /r:System.Drawing.dll /r:System.Web.Extensions.dll "%~dp0src\*.cs"

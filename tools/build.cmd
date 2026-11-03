@echo off
rem Builds tools\bin\invre.exe and CadConvert.exe with the C# compiler that ships with Windows (.NET Framework 4).
set CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe
if not exist "%~dp0bin" mkdir "%~dp0bin"
"%CSC%" /nologo /platform:x64 /optimize /out:"%~dp0bin\invre.exe" /r:Microsoft.CSharp.dll /r:System.Core.dll /r:System.Web.Extensions.dll "%~dp0invre\*.cs" || exit /b 1
"%CSC%" /nologo /platform:x64 /optimize /target:winexe /out:"%~dp0bin\CadConvert.exe" /r:System.Windows.Forms.dll /r:System.Drawing.dll /r:System.Core.dll /r:System.Web.Extensions.dll "%~dp0convert\*.cs" "%~dp0invre\Cfb.cs"

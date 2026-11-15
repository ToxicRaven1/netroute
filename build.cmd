@echo off
rem Build netroute.exe with the C# compiler that ships inside Windows.
rem Nothing to install: every Windows 10/11 PC has this compiler and the
rem .NET Framework 4.8 runtime the exe needs. Output: dist\netroute.exe
setlocal
cd /d "%~dp0"

set CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe
if not exist "%CSC%" set CSC=%WINDIR%\Microsoft.NET\Framework\v4.0.30319\csc.exe
if not exist "%CSC%" (
  echo Could not find csc.exe. The .NET Framework 4.x is part of Windows; is this Windows?
  exit /b 1
)

if not exist dist mkdir dist

"%CSC%" /nologo /target:winexe /optimize+ /platform:anycpu ^
  /out:dist\netroute.exe ^
  /win32manifest:app\app.manifest ^
  /win32icon:app\netroute.ico ^
  /reference:System.Web.Extensions.dll ^
  /define:TRACE ^
  app\*.cs
if errorlevel 1 (
  echo.
  echo BUILD FAILED
  exit /b 1
)
echo.
echo Built dist\netroute.exe

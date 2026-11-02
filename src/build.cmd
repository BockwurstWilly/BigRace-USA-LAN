@echo off
rem Baut dinput.dll (LAN-Infos im Fenstertitel, 32 Bit, Visual C++ Build Tools), BigRace-LAN-Launcher.exe und
rem BigRace-LAN-Patcher.exe (.NET-Framework-4-Compiler aus Windows). DLL und Launcher werden in den
rem Patcher eingebettet.
setlocal
set CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe
if not exist "%CSC%" set CSC=%WINDIR%\Microsoft.NET\Framework\v4.0.30319\csc.exe
cd /d "%~dp0"
if not exist obj mkdir obj

for /f "usebackq delims=" %%i in (`"%ProgramFiles(x86)%\Microsoft Visual Studio\Installer\vswhere.exe" -latest -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath`) do set VSDIR=%%i
if not defined VSDIR (echo Visual C++ Build Tools nicht gefunden & exit /b 1)
call "%VSDIR%\VC\Auxiliary\Build\vcvarsall.bat" x86 >nul || exit /b 1

cl /nologo /O2 /MT /W3 /LD /Foobj\ BarHook.c /Feobj\dinput.dll ^
  /link /DEF:BarHook.def /IMPLIB:obj\dinput.lib user32.lib iphlpapi.lib || exit /b 1

"%CSC%" /nologo /codepage:65001 /target:winexe /platform:anycpu /optimize+ ^
  /out:..\BigRace-LAN-Launcher.exe ^
  /win32manifest:launcher.manifest ^
  /win32icon:icon.ico ^
  /reference:System.Windows.Forms.dll /reference:System.Drawing.dll /reference:System.Core.dll ^
  Launcher.cs Lobby.cs || exit /b 1

"%CSC%" /nologo /codepage:65001 /target:winexe /platform:anycpu /optimize+ ^
  /out:..\BigRace-LAN-Patcher.exe ^
  /win32manifest:app.manifest ^
  /win32icon:icon.ico ^
  /resource:..\BigRace-LAN-Fix.ps1,BigRace-LAN-Fix.ps1 ^
  /resource:..\cnc-ddraw\ddraw.dll,ddraw.dll ^
  /resource:..\cnc-ddraw\ddraw.ini,ddraw.ini ^
  /resource:..\cnc-ddraw\LICENSE.txt,cnc-ddraw-LICENSE.txt ^
  /resource:obj\dinput.dll,dinput.dll ^
  /resource:..\BigRace-LAN-Launcher.exe,BigRace-LAN-Launcher.exe ^
  /reference:System.Windows.Forms.dll /reference:System.Drawing.dll ^
  Patcher.cs

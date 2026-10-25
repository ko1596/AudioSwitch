@echo off
rem Builds with the .NET Framework compiler that ships with Windows (no Visual Studio needed).
rem One program is copied under several names; the file name suffix selects the behavior
rem (-Out / -In / -Profile / -Tray / -Editor).
setlocal
set CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe
set OUT=%~dp0dist
if not exist "%OUT%" mkdir "%OUT%"
set ICON=
if exist "%~dp0src\icon.ico" set ICON=/win32icon:"%~dp0src\icon.ico"

"%CSC%" /nologo /target:winexe /platform:x64 /codepage:65001 %ICON% /out:"%OUT%\AudioSwitch.exe" /r:System.Windows.Forms.dll /r:System.Drawing.dll "%~dp0src\AudioSwitch.cs"
if errorlevel 1 exit /b 1

for %%N in (Out In Profile Tray Editor) do copy /y "%OUT%\AudioSwitch.exe" "%OUT%\AudioSwitch-%%N.exe" >nul
echo Done: %OUT%

@echo off
setlocal
cd /d "%~dp0"
set CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe
set SRC=SM809Driver.cs TrayHost.cs TaskbarBattery.cs MapView.cs NavBar.cs BluetoothBattery.cs
set OPT=-nologo -codepage:65001 -platform:anycpu -r:System.Web.Extensions.dll -win32manifest:app.manifest -win32icon:images\app.ico -resource:images\app.ico,SM809.app.ico
"%CSC%" %OPT% -target:winexe -resource:images\mouse_nr.png,SM809.mouse_nr.png -out:SM809Driver.exe %SRC% || exit /b 1
"%CSC%" %OPT% -target:exe -define:CLI -out:SM809Restore.exe %SRC% || exit /b 1
echo build ok

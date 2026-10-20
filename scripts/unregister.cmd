@echo off
rem Removes the GeoSimplifier add-in registration. Run from an elevated (Administrator) prompt.
setlocal
set DLL=%~1
if "%DLL%"=="" set DLL=%~dp0..\src\GeoSimplifier.SolidWorks\bin\Debug\net48\GeoSimplifier.SolidWorks.dll
"%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\RegAsm.exe" /u "%DLL%"

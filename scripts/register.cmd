@echo off
rem Registers the GeoSimplifier add-in with SOLIDWORKS. Run from an elevated (Administrator) prompt.
rem Usage: register.cmd [path\to\GeoSimplifier.SolidWorks.dll]
setlocal
set DLL=%~1
if "%DLL%"=="" set DLL=%~dp0..\src\GeoSimplifier.SolidWorks\bin\Debug\net48\GeoSimplifier.SolidWorks.dll
"%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\RegAsm.exe" /codebase "%DLL%"

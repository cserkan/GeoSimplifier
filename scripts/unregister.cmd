@echo off
rem Removes the GeoSimplifier add-in registration. Run from an elevated (Administrator) prompt.
setlocal
set DLL=%~1
if "%DLL%"=="" set DLL=%~dp0..\src\GeoSimplifier.SolidWorks\bin\Debug\net48\GeoSimplifier.SolidWorks.dll
if not exist "%DLL%" (
  echo Add-in DLL not found: "%DLL%"
  echo Build it first:  dotnet build src\GeoSimplifier.SolidWorks
  exit /b 1
)
"%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\RegAsm.exe" /u "%DLL%"

@echo off
rem 3DWrap lisans anahtar ciftini BIR KEZ olusturur.
rem Ozel anahtar: %USERPROFILE%\3DWrap-Lisans\3DWrap-ozel-anahtar.xml (yedekleyin, git'e eklemeyin)
rem Acik anahtar: src\GeoSimplifier.SolidWorks\Licensing\3DWrap-acik-anahtar.xml (eklentiye gomulur, git'e eklenir)
setlocal
set ROOT=%~dp0..
dotnet build "%ROOT%\tools\GeoSimplifier.LicenseTool" -c Release -f net48 || exit /b 1
"%ROOT%\tools\GeoSimplifier.LicenseTool\bin\Release\net48\3DWrap-Lisans.exe" anahtar-olustur --acik "%ROOT%\src\GeoSimplifier.SolidWorks\Licensing\3DWrap-acik-anahtar.xml"

@echo off
rem GeoSimplifier SOLIDWORKS eklentisi - kaldirma
setlocal
set HERE=%~dp0
set DLL=%HERE%bin\GeoSimplifier.SolidWorks.dll

net session >nul 2>&1
if errorlevel 1 (
  echo Yonetici izni isteniyor...
  powershell -NoProfile -Command "Start-Process -FilePath '%~f0' -Verb RunAs"
  exit /b
)

if not exist "%DLL%" (
  echo HATA: "%DLL%" bulunamadi.
  goto :bitti
)

echo SOLIDWORKS kapali olmalidir. Acik ise kapatip Enter'a basin.
pause

"%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\RegAsm.exe" /u "%DLL%"
echo.
echo Kayit kaldirildi. Klasoru artik silebilirsiniz.

:bitti
echo.
pause

@echo off
rem GeoSimplifier kurulum seti olusturur: Release derler, DLL'leri ve
rem kurulum betiklerini dist\GeoSimplifier-Eklenti altina kopyalar, zipler.
rem Kullanim (SOLIDWORKS kurulu bir Windows PC'de):  scripts\paket-yap.cmd
setlocal
set ROOT=%~dp0..
set OUT=%ROOT%\dist\GeoSimplifier-Eklenti

echo [1/4] Release derleniyor...
dotnet build "%ROOT%\src\GeoSimplifier.SolidWorks" -c Release
if errorlevel 1 exit /b 1

set BIN=%ROOT%\src\GeoSimplifier.SolidWorks\bin\Release\net48
if not exist "%BIN%\GeoSimplifier.SolidWorks.dll" (
  echo HATA: "%BIN%" altinda derleme cikti bulunamadi.
  exit /b 1
)

echo [2/4] Dosyalar toplaniyor...
if exist "%OUT%" rmdir /s /q "%OUT%"
mkdir "%OUT%\bin"
copy /y "%BIN%\GeoSimplifier.SolidWorks.dll" "%OUT%\bin\" >nul
copy /y "%BIN%\GeoSimplifier.Core.dll" "%OUT%\bin\" >nul
copy /y "%BIN%\SolidWorks.Interop.*.dll" "%OUT%\bin\" >nul
copy /y "%BIN%\*.pdb" "%OUT%\bin\" >nul 2>&1

echo [3/4] Kurulum betikleri kopyalaniyor...
copy /y "%ROOT%\packaging\KUR.cmd" "%OUT%\" >nul
copy /y "%ROOT%\packaging\KALDIR.cmd" "%OUT%\" >nul
copy /y "%ROOT%\packaging\OKU-BENI.txt" "%OUT%\" >nul

echo [4/4] Zip olusturuluyor...
set ZIP=%ROOT%\dist\GeoSimplifier-Kurulum.zip
if exist "%ZIP%" del "%ZIP%"
powershell -NoProfile -Command "Compress-Archive -Path '%OUT%' -DestinationPath '%ZIP%'"
if errorlevel 1 exit /b 1

echo.
echo TAMAM: "%ZIP%"

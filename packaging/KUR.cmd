@echo off
rem GeoSimplifier SOLIDWORKS eklentisi - kurulum
rem Bu dosyayi cift tiklayin. Yonetici izni kendisi ister.
setlocal
set HERE=%~dp0
set DLL=%HERE%bin\GeoSimplifier.SolidWorks.dll

rem --- Yonetici degilse kendini yonetici olarak yeniden baslat ---
net session >nul 2>&1
if errorlevel 1 (
  echo Yonetici izni isteniyor...
  powershell -NoProfile -Command "Start-Process -FilePath '%~f0' -Verb RunAs"
  exit /b
)

echo ============================================
echo   GeoSimplifier - SOLIDWORKS eklenti kurulum
echo ============================================
echo Klasor: %HERE%
echo.

if not exist "%DLL%" (
  echo HATA: "%DLL%" bulunamadi.
  echo Zip dosyasini bir klasore tam olarak cikardiginizdan emin olun.
  goto :bitti
)

set REGASM=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\RegAsm.exe
if not exist "%REGASM%" (
  echo HATA: .NET Framework 4.x 64-bit RegAsm bulunamadi:
  echo   %REGASM%
  echo Windows 10/11 uzerinde normalde vardir. 64-bit Windows gerekir.
  goto :bitti
)

echo SOLIDWORKS kapali olmalidir. Acik ise simdi kapatip Enter'a basin.
pause

echo Onceki kayit (varsa) temizleniyor...
"%REGASM%" /u "%DLL%" >nul 2>&1

echo Eklenti kaydediliyor...
"%REGASM%" /codebase "%DLL%"
if errorlevel 1 (
  echo.
  echo HATA: Kayit basarisiz. Yukaridaki RegAsm mesajina bakin.
  goto :bitti
)

echo.
echo TAMAM. Kurulum bitti.
echo.
echo ONEMLI: Bu klasoru BASKA YERE TASIMAYIN veya SILMEYIN.
echo Eklenti dogrudan buradaki DLL'den calisir. Tasirsaniz KUR.cmd'yi
echo yeni yerde tekrar calistirin.
echo.
echo Sonraki adim: SOLIDWORKS'u acin, Araclar ^> Eklentiler listesinde
echo "GeoSimplifier"i isaretleyin (saga da tik koyarsaniz her acilista yuklenir).
echo Sonra bir parca/montaj acip CommandManager'daki GeoSimplifier sekmesinden
echo "Shrink Wrap" komutunu calistirin.

:bitti
echo.
pause

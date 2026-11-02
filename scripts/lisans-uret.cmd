@echo off
rem 3DWrap lisans dosyasi uretir. Ornek:
rem   scripts\lisans-uret.cmd --firma "ABC Makina A.S." --seri "9000 0000 1111 2222 ABCD EFGH" --bitis 2027-12-31
rem Dosya %USERPROFILE%\3DWrap-Lisans\Lisanslar\ altina yazilir (--cikti ile degistirilebilir).
setlocal
set ROOT=%~dp0..
set EXE=%ROOT%\tools\GeoSimplifier.LicenseTool\bin\Release\net48\3DWrap-Lisans.exe
if not exist "%EXE%" dotnet build "%ROOT%\tools\GeoSimplifier.LicenseTool" -c Release -f net48 >nul || exit /b 1
"%EXE%" uret %*

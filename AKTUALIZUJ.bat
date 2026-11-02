@echo off
title Mody Burglin' Gnomes - aktualizacja
rem Wrzuc ten plik do folderu gry (tam gdzie Gnomium.exe) albo gdziekolwiek - sam znajdzie gre.
rem Pobiera najnowszy aktualizator z GitHuba i uruchamia go.
powershell -NoProfile -ExecutionPolicy Bypass -Command ^
  "[Net.ServicePointManager]::SecurityProtocol='Tls12';" ^
  "$p = Join-Path $env:TEMP 'gnome_mods_aktualizuj.ps1';" ^
  "try { Invoke-WebRequest -UseBasicParsing 'https://raw.githubusercontent.com/Viamio/mody-do-gnomow-/main/scripts/aktualizuj.ps1' -OutFile $p } catch { Write-Host ('[BLAD] Brak polaczenia z GitHubem: ' + $_.Exception.Message) -ForegroundColor Red; exit 1 };" ^
  "& $p -StartDir '%~dp0'"
echo.
pause

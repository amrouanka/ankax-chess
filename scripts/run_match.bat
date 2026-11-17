@echo off
setlocal

set "ANKAX_DEV=%~dp0..\bin\Release\net10.0\AnkaX.exe"
set "ANKAX_RELEASE=%~dp0..\releases\AnkaX_1.0.7.exe"
set "BOOK=%~dp0..\data\noob_3moves.epd"
set "PGN=%~dp0..\dev_vs_release.pgn"

:: Cutechess must be installed separately [EDIT if different]
set "CUTECHESS=C:\Program Files (x86)\Cute Chess\cutechess-cli.exe"

:: ============================================================
::  SPRT MODE SELECTION (Uncomment ONLY ONE option below)
:: ============================================================
:: 1) Standard STC Gain Test (Testing a new feature for +Elo)
:: set "SPRT_ARGS=-sprt elo0=0.0 elo1=2.0 alpha=0.05 beta=0.05"

:: 2) Non-Regression Test (Testing code refactors/cleanups)
set "SPRT_ARGS=-sprt elo0=-1.75 elo1=0.25 alpha=0.05 beta=0.05"

:: 3) Disabled (Run fixed number of games without early stopping)
:: set "SPRT_ARGS="
:: ============================================================

if not exist "%ANKAX_DEV%" (
    echo [!] Dev build not found: "%ANKAX_DEV%"
    echo     Run: dotnet build -c Release
    pause
    exit /b 1
)
if not exist "%ANKAX_RELEASE%" (
    echo [!] Release build not found: "%ANKAX_RELEASE%"
    pause
    exit /b 1
)
if not exist "%CUTECHESS%" (
    echo [!] Cutechess not found at: "%CUTECHESS%"
    echo     Install from: https://github.com/cutechess/cutechess/releases
    pause
    exit /b 1
)
if not exist "%BOOK%" (
    echo [!] Opening book not found: "%BOOK%"
    pause
    exit /b 1
)

echo ============================================
echo   AnkaX Dev vs AnkaX Release (STC Test)
echo ============================================
echo.

"%CUTECHESS%" ^
    -engine name=AnkaX-Dev cmd="%ANKAX_DEV%" proto=uci ^
    -engine name=AnkaX cmd="%ANKAX_RELEASE%" proto=uci ^
    -each tc=10+0.1 ^
    -openings file="%BOOK%" format=epd order=random ^
    -games 20000 -concurrency 4 -repeat -recover ^
    -resign movecount=3 score=400 ^
    -draw movenumber=34 movecount=8 score=8 ^
    %SPRT_ARGS% ^
    -pgnout "%PGN%"

echo.
echo ============================================
echo   Match complete.
echo   PGN saved as dev_vs_release.pgn in repo root.
echo ============================================
pause
@echo off
setlocal

set "ANKAX_DEV=%~dp0bin\Release\net10.0\win-x64\publish\AnkaX.exe"
set "ANKAX_RELEASE=%~dp0releases\AnkaX_1.0.5.exe"
set "BOOK=%~dp0books\UHO_4060_v4.epd"
set "PGN=%~dp0dev_vs_release.pgn"

:: Cutechess must be installed separately [EDIT if different]
set "CUTECHESS=C:\Program Files (x86)\Cute Chess\cutechess-cli.exe"

if not exist "%ANKAX_DEV%" (
    echo [!] Dev build not found. Run: dotnet publish -c Release -r win-x64
    pause & exit /b 1
)
if not exist "%ANKAX_RELEASE%" (
    echo [!] Release build not found in releases\
    pause & exit /b 1
)
if not exist "%CUTECHESS%" (
    echo [!] Cutechess not found. Install from:
    echo     https://github.com/cutechess/cutechess/releases
    pause & exit /b 1
)
if not exist "%BOOK%" (
    echo [!] Opening book not found in books\
    pause & exit /b 1
)

echo ============================================
echo   AnkaX Dev vs AnkaX Release (10000 games)
echo ============================================
echo.

"%CUTECHESS%" ^
    -engine name=AnkaX-Dev cmd="%ANKAX_DEV%" proto=uci ^
    -engine name=AnkaX cmd="%ANKAX_RELEASE%" proto=uci ^
    -each tc=3+0.05 ^
    -openings file="%BOOK%" format=epd order=random plies=12 ^
    -games 10000 -concurrency 4 -repeat -recover ^
    -resign movecount=3 score=400 ^
    -draw movenumber=30 movecount=5 score=10 ^
    -maxmoves 200 ^
    -pgnout "%PGN%"

echo.
echo ============================================
echo   Match complete.
echo   PGN saved as dev_vs_release.pgn
echo ============================================
pause
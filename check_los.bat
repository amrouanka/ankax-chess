@echo off
setlocal
set "PGN=%~dp0combined.pgn"

if not exist "%PGN%" (
    echo.
    echo   [!] PGN not found: %PGN%
    echo.
    pause
    exit /b
)

powershell -NoProfile -Command ^
  "$t=Get-Content -Raw '%PGN%';" ^
  "$w=[regex]::Matches($t,'\[White \"(.+?)\"\]');" ^
  "$r=[regex]::Matches($t,'\[Result \"(.+?)\"\]');" ^
  "$wn=0;$ls=0;$dr=0;" ^
  "for($i=0;$i -lt $w.Count;$i++){" ^
  "$dw=$w[$i].Groups[1].Value -match 'Dev';" ^
  "$rs=$r[$i].Groups[1].Value;" ^
  "if($rs -eq '1/2-1/2'){$dr++}" ^
  "elseif(($dw -and $rs -eq '1-0') -or (-not $dw -and $rs -eq '0-1')){$wn++}" ^
  "elseif(($dw -and $rs -eq '0-1') -or (-not $dw -and $rs -eq '1-0')){$ls++}}" ^
  "$n=$wn+$ls+$dr;" ^
  "if($n -eq 0){Write-Host 'No games found.';exit};" ^
  "$p=($wn+$dr/2)/$n;" ^
  "$elo=[math]::Round(-400*[math]::Log10(1/$p-1),1);" ^
  "$v=($wn*[math]::Pow(1-$p,2)+$dr*[math]::Pow(0.5-$p,2)+$ls*[math]::Pow($p,2))/($n-1);" ^
  "$ee=[math]::Round(1.959964*400/([math]::Log(10)*$p*(1-$p))*[math]::Sqrt($v/$n),1);" ^
  "$z=if($wn+$ls){($wn-$ls)/[math]::Sqrt($wn+$ls)}else{0};" ^
  "$az=[math]::Abs($z);" ^
  "$tt=1/(1+0.2316419*$az);" ^
  "$phi=[math]::Exp(-$az*$az/2)/[math]::Sqrt(2*[math]::PI);" ^
  "$cdf=1-$phi*(0.319381530*$tt-0.356563782*$tt*$tt+1.781477937*$tt*$tt*$tt-1.821255978*$tt*$tt*$tt*$tt+1.330274429*$tt*$tt*$tt*$tt*$tt);" ^
  "if($z -lt 0){$cdf=1-$cdf};" ^
  "$los=[math]::Round($cdf*100,1);" ^
  "$drawrate=[math]::Round($dr/$n*100,1);" ^
  "$sw=[string]$wn;$sl=[string]$ls;" ^
  "Write-Host '';" ^
  "Write-Host '============================================';" ^
  "Write-Host '       AnkaX Dev vs Release - Results       ';" ^
  "Write-Host '============================================';" ^
  "Write-Host ('  Games:       '+$n);" ^
  "Write-Host ('  AnkaX-Dev:   '+$sw.PadLeft(4)+'    AnkaX:   '+$sl.PadLeft(4)+'    Draws: '+$dr);" ^
  "Write-Host '--------------------------------------------';" ^
  "Write-Host ('  Elo:         '+$elo+' +/- '+$ee);" ^
  "Write-Host ('  LOS:         '+$los+' %%');" ^
  "Write-Host ('  Draw rate:   '+$drawrate+' %%');" ^
  "Write-Host '============================================';" ^
  "Write-Host ''"
pause
@echo off
setlocal
set "PGN=%~dp0dev_vs_release.pgn"

if not exist "%PGN%" (
    echo.
    echo   [!] PGN not found: %PGN%
    echo.
    pause
    exit /b
)

powershell -NoProfile -Command ^
  "$c=Get-Content '%PGN%';$wh='';$rs='';$wn=0;$ls=0;$dr=0;" ^
  "foreach($ln in $c){" ^
  "if($ln -match '\[White \"(.+)\"\]'){$wh=$matches[1]};" ^
  "if($ln -match '\[Result \"(.+)\"\]'){$rs=$matches[1]};" ^
  "if($rs -ne ''){" ^
  "$dw=$wh -match 'Dev';" ^
  "if($rs -eq '1/2-1/2'){$dr++}" ^
  "elseif(($dw -and $rs -eq '1-0') -or (-not $dw -and $rs -eq '0-1')){$wn++}" ^
  "else{$ls++};" ^
  "$rs=''}};" ^
  "$n=$wn+$ls+$dr;" ^
  "if($n -eq 0){Write-Host 'No games found.';exit};" ^
  "$p=($wn+$dr/2)/$n;" ^
  "$elo=[math]::Round(-400*[math]::Log10(1/$p-1),1);" ^
  "$v=($wn*[math]::Pow(1-$p,2)+$dr*[math]::Pow(0.5-$p,2)+$ls*[math]::Pow($p,2))/($n-1);" ^
  "$ee=[math]::Round(1.959964*400/([math]::Log(10)*$p*(1-$p))*[math]::Sqrt($v/$n),1);" ^
  "$z=if($wn+$ls){($wn-$ls)/[math]::Sqrt($wn+$ls)}else{0};" ^
  "$az=[math]::Abs($z);" ^
  "$t=1/(1+0.2316419*$az);" ^
  "$phi=[math]::Exp(-$az*$az/2)/[math]::Sqrt(2*[math]::PI);" ^
  "$cdf=1-$phi*(0.319381530*$t-0.356563782*$t*$t+1.781477937*$t*$t*$t-1.821255978*$t*$t*$t*$t+1.330274429*$t*$t*$t*$t*$t);" ^
  "if($z -lt 0){$cdf=1-$cdf};" ^
  "$los=[math]::Round($cdf*100,1);" ^
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
  "Write-Host '============================================';" ^
  "Write-Host ''"
pause
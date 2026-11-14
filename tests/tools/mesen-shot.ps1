# Runs a .nes image in Mesen's test runner (no window) and saves a screenshot after the given frames.
#
#   pwsh tests/tools/mesen-shot.ps1 game.nes shot.png 60 [more frames...]
#
# With more than one frame count, a screenshot is saved at each, named shot-<frame>.png. Mesen is at
# C:\Mesen, in portable mode, with scripts' file access on (CLAUDE.md). The script stops Mesen with
# exit code 0; a script error never stops it, so the run is limited to 60 seconds.
param(
    [Parameter(Mandatory)] [string] $Image,
    [Parameter(Mandatory)] [string] $Shot,
    [Parameter(Mandatory, ValueFromRemainingArguments)] [int[]] $Frames
)
$ErrorActionPreference = 'Stop'
$image = (Resolve-Path $Image).Path
$shot = [IO.Path]::GetFullPath($Shot)
$base = [IO.Path]::ChangeExtension($shot, $null).TrimEnd('.')
$names = @(foreach ($f in $Frames) { if ($Frames.Count -eq 1) { $shot } else { "$base-$f.png" } })
$pairs = for ($i = 0; $i -lt $Frames.Count; $i++) { "[$($Frames[$i])] = [[$($names[$i])]]" }
$last = ($Frames | Measure-Object -Maximum).Maximum
$lua = @"
local shots = { $($pairs -join ', ') }
local n = 0
emu.addEventCallback(function()
  n = n + 1
  if shots[n] then
    local f = io.open(shots[n], "wb")
    f:write(emu.takeScreenshot()); f:close()
  end
  if n >= $last then emu.stop(0) end
end, emu.eventType.endFrame)
"@
$script = Join-Path ([IO.Path]::GetTempPath()) "mesen-shot-$PID.lua"
Set-Content -Path $script -Value $lua -Encoding utf8
try {
    $p = Start-Process -FilePath 'C:\Mesen\Mesen.exe' -ArgumentList @('--testRunner', "`"$image`"", "`"$script`"") -PassThru -NoNewWindow
    if (-not $p.WaitForExit(60000)) { $p.Kill(); throw "Mesen did not stop within 60 seconds (a script error?)" }
    if ($p.ExitCode -ne 0) { throw "Mesen exited with code $($p.ExitCode)" }
    $names | ForEach-Object { if (-not (Test-Path $_)) { throw "No screenshot at $_" }; Write-Output "Saved $_" }
}
finally { Remove-Item $script -ErrorAction SilentlyContinue }

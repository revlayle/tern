# Runs a program in Altirra (portable mode: C:\Altirra\Altirra.ini), saves a screenshot of its window
# after a few seconds, then closes it. Altirra needs a window: its debugger has no screenshot or quit.
#   pwsh tests/tools/altirra-shot.ps1 <file.xex> <shot.png> <seconds> <altirra switches...>
# e.g. ... examples\atari\dli.xex shot.png 8 /skipsetup /hardware:800xl /kernel:default /memsize:64K /nobasic
param([string]$Xex, [string]$Shot, [int]$Seconds, [Parameter(ValueFromRemainingArguments)][string[]]$Switches)

Add-Type -AssemblyName System.Drawing
Add-Type @"
using System;
using System.Runtime.InteropServices;
public static class Win {
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
    public struct RECT { public int Left, Top, Right, Bottom; }
}
"@

$p = Start-Process -FilePath 'C:\Altirra\Altirra64.exe' -ArgumentList ($Switches + @('/run', "`"$Xex`"")) -PassThru
Start-Sleep -Seconds $Seconds
$p.Refresh()
$h = $p.MainWindowHandle
[void][Win]::SetForegroundWindow($h)
Start-Sleep -Milliseconds 500
$r = New-Object Win+RECT
[void][Win]::GetWindowRect($h, [ref]$r)
$bmp = New-Object System.Drawing.Bitmap ($r.Right - $r.Left), ($r.Bottom - $r.Top)
$g = [System.Drawing.Graphics]::FromImage($bmp)
$g.CopyFromScreen($r.Left, $r.Top, 0, 0, $bmp.Size)
$bmp.Save($Shot, [System.Drawing.Imaging.ImageFormat]::Png)
"title: " + $p.MainWindowTitle
if (-not $p.CloseMainWindow()) { $p | Stop-Process -Force }
if (-not $p.WaitForExit(5000)) { $p | Stop-Process -Force }
"saved $Shot"

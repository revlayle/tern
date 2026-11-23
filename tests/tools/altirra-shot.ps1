# Runs a program in Altirra (portable mode: C:\Altirra\Altirra.ini), saves a screenshot of its window
# after a few seconds, then closes it. Altirra needs a window: its debugger has no screenshot or quit. The window's
# client area (the emulated screen, without the title bar, whose colour changes with focus, or the menu) is captured
# with PrintWindow, so it need not be in front (other windows over it are not in the picture).
#   pwsh tests/tools/altirra-shot.ps1 <file.xex> <shot.png> <seconds> <altirra switches...>
# e.g. ... examples\atari\dli.xex shot.png 8 /skipsetup /hardware:800xl /kernel:default /memsize:64K /nobasic
param([string]$Xex, [string]$Shot, [int]$Seconds, [Parameter(ValueFromRemainingArguments)][string[]]$Switches)

Add-Type -AssemblyName System.Drawing
Add-Type @"
using System;
using System.Runtime.InteropServices;
public static class Win {
    [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr h, IntPtr dc, uint flags);
    [DllImport("user32.dll")] public static extern bool GetClientRect(IntPtr h, out RECT r);
    public struct RECT { public int Left, Top, Right, Bottom; }
}
"@

$p = Start-Process -FilePath 'C:\Altirra\Altirra64.exe' -ArgumentList ($Switches + @('/run', "`"$Xex`"")) -PassThru
Start-Sleep -Seconds $Seconds
$p.Refresh()
$h = $p.MainWindowHandle
Start-Sleep -Milliseconds 500
$r = New-Object Win+RECT
[void][Win]::GetClientRect($h, [ref]$r)
$bmp = New-Object System.Drawing.Bitmap ($r.Right - $r.Left), ($r.Bottom - $r.Top)
$g = [System.Drawing.Graphics]::FromImage($bmp)
$dc = $g.GetHdc()
[void][Win]::PrintWindow($h, $dc, 3)            # PW_CLIENTONLY (1) | PW_RENDERFULLCONTENT (2): as drawn
$g.ReleaseHdc($dc)
$bmp.Save($Shot, [System.Drawing.Imaging.ImageFormat]::Png)
"title: " + $p.MainWindowTitle
if (-not $p.CloseMainWindow()) { $p | Stop-Process -Force }
if (-not $p.WaitForExit(5000)) { $p | Stop-Process -Force }
"saved $Shot"

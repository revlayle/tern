# Boots a disk image in AppleWin (portable mode: C:\AppleWin\AppleWin.ini), saves a screenshot of its
# window after a few seconds, then closes it. AppleWin needs a window, as Altirra does. The window is
# captured with PrintWindow, so other windows in front of it do not matter.
#   pwsh tests/tools/applewin-shot.ps1 <disk.po> <shot.png> <seconds> <AppleWin switches...>
# e.g. ... disk.po shot.png 10 -model apple2ee
#      ... disk.po shot.png 10 -model apple2p -s0 languagecard
# The disk goes in slot 6, drive 1 (-s7 empty -d1), sound is off (-m), and no message box asks
# where to save a screenshot (-no-printscreen-dlg). Make the disk with apple2-disk.ps1.
param([string]$Disk, [string]$Shot, [int]$Seconds, [Parameter(ValueFromRemainingArguments)][string[]]$Switches)

Add-Type -AssemblyName System.Drawing
Add-Type @"
using System;
using System.Runtime.InteropServices;
public static class Win {
    [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr h, IntPtr dc, uint flags);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
    public struct RECT { public int Left, Top, Right, Bottom; }
}
"@

$arguments = @('-conf', 'C:\AppleWin\AppleWin.ini') + $Switches + @('-s7', 'empty', '-d1', "`"$([IO.Path]::GetFullPath($Disk))`"", '-no-printscreen-dlg', '-m')
$p = Start-Process -FilePath 'C:\AppleWin\AppleWin-x64.exe' -ArgumentList $arguments -PassThru
Start-Sleep -Seconds $Seconds
$p.Refresh()
$h = $p.MainWindowHandle
$r = New-Object Win+RECT
[void][Win]::GetWindowRect($h, [ref]$r)
$bmp = New-Object System.Drawing.Bitmap ($r.Right - $r.Left), ($r.Bottom - $r.Top)
$g = [System.Drawing.Graphics]::FromImage($bmp)
$dc = $g.GetHdc()
[void][Win]::PrintWindow($h, $dc, 2)      # PW_RENDERFULLCONTENT
$g.ReleaseHdc($dc)
$bmp.Save($Shot, [System.Drawing.Imaging.ImageFormat]::Png)
"title: " + $p.MainWindowTitle
if (-not $p.CloseMainWindow()) { $p | Stop-Process -Force }
if (-not $p.WaitForExit(5000)) { $p | Stop-Process -Force }
"saved $Shot"

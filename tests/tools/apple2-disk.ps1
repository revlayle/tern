# Puts a Tern program's ProDOS system file on a bootable ProDOS 2.4.3 disk image for AppleWin.
#
#   pwsh tests/tools/apple2-disk.ps1 program.system disk.po
#
# The disk is a copy of AppleWin's ProDOS_2_4_3.po that holds only PRODOS and the program, so the
# program is the first .SYSTEM file and ProDOS starts it at boot. Its ProDOS name is the file's name
# in capitals, cut to 8 letters and digits, plus ".SYSTEM" (type SYS $FF, aux type $2000).
# Uses CiderPress II's cp2.
param(
    [Parameter(Mandatory)] [string] $Program,
    [Parameter(Mandatory)] [string] $Disk,
    [string] $Prodos = "C:\AppleWin\ProDOS_2_4_3.po",
    [string] $Cp2 = "C:\Program Files\CiderPress\cp2.exe"
)
$ErrorActionPreference = "Stop"

function Cp2 {
    & $Cp2 @args
    if ($LASTEXITCODE -ne 0) { throw "cp2 $($args -join ' ') failed" }
}

$Disk = [IO.Path]::GetFullPath($Disk)
Copy-Item $Prodos $Disk -Force
# Every file but PRODOS goes, so the program's entry is the directory's first .SYSTEM file.
$names = (& $Cp2 list $Disk) | Where-Object { $_ -and $_ -ne "PRODOS" }
if ($names) { Cp2 delete $Disk @names }

$base = ([IO.Path]::GetFileNameWithoutExtension($Program).ToUpperInvariant() -replace '[^A-Z0-9]', '')
if ($base -notmatch '^[A-Z]') { $base = "T" + $base }
if ($base.Length -gt 8) { $base = $base.Substring(0, 8) }
$staging = Join-Path ([IO.Path]::GetTempPath()) ("tern-apple2-" + [Guid]::NewGuid())
New-Item -ItemType Directory $staging | Out-Null
try {
    Copy-Item $Program (Join-Path $staging "$base.SYSTEM#ff2000")
    # Added by a bare name, from its folder: cp2 would otherwise make the path's folders on the disk.
    Push-Location $staging
    try { Cp2 add $Disk "$base.SYSTEM#ff2000" } finally { Pop-Location }
}
finally { Remove-Item -Recurse -Force $staging }
Cp2 catalog $Disk

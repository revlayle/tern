# Puts a Tern program's ProDOS system file on a bootable ProDOS 2.4.3 disk image for AppleWin.
#
#   pwsh tests/tools/apple2-disk.ps1 program.system disk.po
#   pwsh tests/tools/apple2-disk.ps1 program.system disk.po level1.bin@`$6000 level2.bin@`$6000
#
# The disk is a copy of AppleWin's ProDOS_2_4_3.po that holds only PRODOS and the program, so the
# program is the first .SYSTEM file and ProDOS starts it at boot. Its ProDOS name is the file's name
# in capitals, cut to 8 letters and digits, plus ".SYSTEM" (type SYS $FF, aux type $2000).
# Overlays and binaries (.bin) follow, each with its load address after '@' (the overlay area's
# first address, as the compiler's report gives it: "level1.bin: ... ($6000-$60FF)"). Each goes on
# the disk as a BIN file (type $06) with that address as its aux type, named as the file in capitals
# (LEVEL1.BIN), so io.load_bin loads it there. Uses CiderPress II's cp2.
[CmdletBinding(PositionalBinding = $false)]
param(
    [Parameter(Mandatory, Position = 0)] [string] $Program,
    [Parameter(Mandatory, Position = 1)] [string] $Disk,
    [Parameter(ValueFromRemainingArguments)] [string[]] $Files = @(),
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
    $names = @("$base.SYSTEM#ff2000")
    Copy-Item $Program (Join-Path $staging $names[0])
    foreach ($file in $Files) {
        if ($file -notmatch '^(.+)@\$?([0-9A-Fa-f]{1,4})$') { throw "Give each .bin file with its load address: level1.bin@`$6000, not $file" }
        $path, $aux = $Matches[1], [Convert]::ToInt32($Matches[2], 16).ToString("x4")
        $name = [IO.Path]::GetFileName($path).ToUpperInvariant()
        if ($name -notmatch '^[A-Z][A-Z0-9.]{0,14}$') { throw "$name is not a ProDOS name (1-15 letters, digits and dots, starting with a letter)" }
        $names += "$name#06$aux"
        Copy-Item $path (Join-Path $staging $names[-1])
    }
    # Added by bare names, from their folder: cp2 would otherwise make the paths' folders on the disk.
    Push-Location $staging
    try { Cp2 add $Disk @names } finally { Pop-Location }
}
finally { Remove-Item -Recurse -Force $staging }
Cp2 catalog $Disk

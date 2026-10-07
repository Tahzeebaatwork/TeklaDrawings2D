$root = Split-Path -Parent $PSScriptRoot
$bin = Join-Path $root "bin\x64\Release\net48"
Add-Type -Path (Join-Path $bin "Tekla.Structures.dll")
Add-Type -Path (Join-Path $bin "Tekla.Structures.Model.dll")

$m = New-Object Tekla.Structures.Model.Model
if ($m.GetConnectionStatus()) {
    Write-Host "Connected to Tekla!"
    $opts = @("XSDATADIR", "XS_MACRO_DIRECTORY", "XS_FIRM", "XS_PROJECT", "XS_SYSTEM")
    foreach ($opt in $opts) {
        $val = ""
        [Tekla.Structures.TeklaStructuresSettings]::GetAdvancedOption($opt, [ref]$val)
        Write-Host "$opt = $val"
    }
} else {
    Write-Host "Not connected to Tekla"
}

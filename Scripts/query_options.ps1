Add-Type -Path "c:\Users\ASUS\Desktop\2d_tekla\bin\Release\net48\Tekla.Structures.dll"
Add-Type -Path "c:\Users\ASUS\Desktop\2d_tekla\bin\Release\net48\Tekla.Structures.Model.dll"

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

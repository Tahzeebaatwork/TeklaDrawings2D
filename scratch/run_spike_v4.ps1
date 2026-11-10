$proc = Start-Process -FilePath "D:\Github\TeklaDrawings2D\bin\x64\Release\net48\ProbeH1.exe" -RedirectStandardOutput "D:\Github\TeklaDrawings2D\scratch\spike_v4_output.txt" -RedirectStandardError "D:\Github\TeklaDrawings2D\scratch\spike_v4_err.txt" -NoNewWindow -PassThru
$finished = $proc.WaitForExit(90000)
if (-not $finished) {
    Write-Host "TIMEOUT: Process did not exit within 90s, killing..."
    $proc.Kill()
    exit 1
}
Write-Host "Exit Code: " $proc.ExitCode
exit $proc.ExitCode

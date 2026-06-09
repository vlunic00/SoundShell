$proj = 'c:\Users\vedro\Documents\Development\SoundShell\src\SoundShell.PoC\SoundShell.PoC.csproj'

Write-Output "Taking snapshot from $proj"
$output = dotnet run --project $proj -- list
$lines = $output -split "`n"
$snap = @()
foreach ($line in $lines) {
    if ($line -match '^\[(\d+)\].*Volume=([0-9]+)%') {
        $snap += [pscustomobject]@{Index=$matches[1]; Volume=$matches[2]}
    }
}

$snapshotPath = Join-Path $PSScriptRoot 'snapshot.json'
$snap | ConvertTo-Json | Out-File $snapshotPath -Encoding utf8
Write-Output "Snapshot saved to $snapshotPath"

Write-Output "Starting watch (background)"
$proc = Start-Process -FilePath 'dotnet' -ArgumentList 'run','--project',$proj,'--','watch' -PassThru
Start-Sleep -Seconds 1

Write-Output "Changing session 1 volume to 50%"
dotnet run --project $proj -- set-volume 1 0.5
Start-Sleep -Seconds 1

Write-Output "Listing sessions after change"
dotnet run --project $proj -- list

Write-Output "Restoring volumes from snapshot"
$snap2 = Get-Content $snapshotPath | ConvertFrom-Json
foreach ($item in $snap2) {
    $dec = [double]$item.Volume / 100.0
    Write-Output "Restoring Index $($item.Index) -> $dec"
    dotnet run --project $proj -- set-volume $item.Index $dec
}

Write-Output "Stopping watch"
Stop-Process -Id $proc.Id -Force
Write-Output 'RESTORE_DONE'

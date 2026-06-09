$proj = 'c:\Users\vedro\Documents\Development\SoundShell\src\SoundShell.PoC\SoundShell.PoC.csproj'
$logsDir = Join-Path (Join-Path (Get-Item $proj).Directory.FullName '..\..\..\bin\Debug\net10.0-windows') 'logs'
if (-not (Test-Path $logsDir)) { $logsDir = Join-Path (Get-Location) 'src\SoundShell.PoC\bin\Debug\net10.0-windows\logs' }

Write-Output "Using logs directory: $logsDir"

# take snapshot
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

# start watch and capture output
Write-Output "Starting watch (background)"
$proc = Start-Process -FilePath 'dotnet' -ArgumentList 'run','--project',$proj,'--','watch' -PassThru
Start-Sleep -Seconds 1

# change session 1 volume to 30%
Write-Output "Changing session 1 volume to 30%"
dotnet run --project $proj -- set-volume 1 0.3

# wait for event in logs (up to 10s)
Write-Output "Waiting for event log..."
$found = $false
for ($i=0; $i -lt 20; $i++) {
    Start-Sleep -Seconds 0.5
    $files = Get-ChildItem -Path $logsDir -Filter "soundshell-*.log" -ErrorAction SilentlyContinue | Sort-Object LastWriteTime -Descending
    if ($files -and $files[0]) {
        $content = Get-Content $files[0].FullName -Raw -ErrorAction SilentlyContinue
        if ($content -match 'OnSimpleVolumeChanged|VolumeChanged|Registered per-session audio events') {
            Write-Output "Found event in log"
            $found = $true
            break
        }
    }
}

if (-not $found) { Write-Warning "No event found in logs within timeout" }

# restore volumes
Write-Output "Restoring volumes from snapshot"
$snap2 = Get-Content $snapshotPath | ConvertFrom-Json
foreach ($item in $snap2) {
    $dec = [double]$item.Volume / 100.0
    Write-Output "Restoring Index $($item.Index) -> $dec"
    dotnet run --project $proj -- set-volume $item.Index $dec
}

Write-Output "Stopping watch"
Stop-Process -Id $proc.Id -Force
Write-Output 'INTEGRATION_DONE'

# Capture the ArcPanel firmware's startup manifest into a tracked, secret-free JSON file.
# Example: .\tools\Capture-ArduinoBuildInfo.ps1 -Port COM6
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$Port,
    [int]$BaudRate = 115200,
    [int]$TimeoutSeconds = 30,
    [string]$OutputPath = (Join-Path $PSScriptRoot '..\docs\BuildEnvironment.json')
)
$ErrorActionPreference = 'Stop'
$inside = $false
$payload = $null
$receivedLines = 0
$preview = [System.Collections.Generic.List[string]]::new()
$buffer = ''
$watch = [System.Diagnostics.Stopwatch]::StartNew()
$serial = $null
$completed = $false

try {
    Write-Host "Listening on $Port. Press the ESP32 RESET button after the port opens."
    while ($watch.Elapsed.TotalSeconds -lt $TimeoutSeconds -and -not $completed) {
        if ($null -eq $serial -or -not $serial.IsOpen) {
            try {
                if ($null -ne $serial) { $serial.Dispose() }
                $serial = [System.IO.Ports.SerialPort]::new($Port, $BaudRate)
                $serial.ReadTimeout = 500
                $serial.DtrEnable = $true
                $serial.RtsEnable = $false
                $serial.Open()
                Write-Verbose "Opened $Port"
            }
            catch {
                Write-Verbose "Waiting for serial port: $($_.Exception.Message)"
                Start-Sleep -Milliseconds 400
                continue
            }
        }
        try {
            $chunk = $serial.ReadExisting()
            if (-not $chunk) { Start-Sleep -Milliseconds 50; continue }
            $buffer += $chunk
            while ($buffer.Contains("`n")) {
                $index = $buffer.IndexOf("`n")
                $line = $buffer.Substring(0, $index).Trim()
                $buffer = $buffer.Substring($index + 1)
                if (-not $line) { continue }
                $receivedLines++
                if ($preview.Count -lt 12) { $preview.Add($line) }
                Write-Verbose "Serial: $line"
                if ($line -eq 'ARC_BUILD_INFO_BEGIN') {
                    $inside = $true
                    $payload = $null
                    continue
                }
                if ($line -eq 'ARC_BUILD_INFO_END' -and $inside) {
                    $completed = $true
                    break
                }
                if ($inside -and $line.StartsWith('{')) { $payload = $line }
            }
        }
        catch {
            # ESP32-S3 native USB can briefly drop or cancel reads during reset.
            Write-Verbose "Serial connection interrupted: $($_.Exception.Message). Reopening..."
            try { $serial.Close() } catch {}
            $serial.Dispose()
            $serial = $null
            $buffer = ''
            Start-Sleep -Milliseconds 300
        }
    }
    if (-not $completed -or -not $payload) {
        $sample = $preview -join ' | '
        throw "No complete firmware build manifest received in $TimeoutSeconds seconds ($receivedLines lines). First lines: $sample. Upload the latest firmware, enable USB CDC On Boot, and press RESET while listening."
    }
}
finally {
    if ($null -ne $serial) {
        try { if ($serial.IsOpen) { $serial.Close() } } catch {}
        $serial.Dispose()
    }
}
$firmware = $payload | ConvertFrom-Json
if ($firmware.schemaVersion -ne 1 -or -not $firmware.arduinoCoreVersion) {
    throw 'Invalid firmware build manifest; output file was not changed.'
}
# IDE version is host-side metadata, not compiled into firmware.
$ideVersion = $null
$ideCandidates = @(
    (Join-Path $env:LOCALAPPDATA 'Programs\Arduino IDE\resources\app\package.json'),
    (Join-Path $env:ProgramFiles 'Arduino IDE\resources\app\package.json')
)
foreach ($candidate in $ideCandidates) {
    if (Test-Path $candidate) {
        try { $ideVersion = (Get-Content $candidate -Raw | ConvertFrom-Json).version; if ($ideVersion) { break } }
        catch { Write-Warning "Cannot read IDE metadata at $candidate" }
    }
}
$corePackage = $null
$coreRoot = Join-Path $env:LOCALAPPDATA 'Arduino15\packages\esp32\hardware\esp32'
if (Test-Path $coreRoot) {
    $corePackage = Get-ChildItem $coreRoot -Directory | Select-Object -ExpandProperty Name
}
$result = [ordered]@{
    schemaVersion = 1
    capturedAtUtc = (Get-Date).ToUniversalTime().ToString('o')
    arduinoIdeVersion = $ideVersion
    installedEsp32CorePackages = @($corePackage)
    firmware = $firmware
}
$resolved = [System.IO.Path]::GetFullPath($OutputPath)
$folder = Split-Path $resolved -Parent
if (-not (Test-Path $folder)) { New-Item -ItemType Directory -Path $folder -Force | Out-Null }
$result | ConvertTo-Json -Depth 8 | Set-Content -Path $resolved -Encoding UTF8
Write-Host "Build manifest saved: $resolved"
Write-Host 'Review and commit the JSON file using GitHub Desktop.'

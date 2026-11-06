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
$serial = [System.IO.Ports.SerialPort]::new($Port, $BaudRate)
$serial.ReadTimeout = 1000
$serial.DtrEnable = $true
$serial.RtsEnable = $false
$inside = $false
$payload = $null
$receivedLines = 0
$preview = [System.Collections.Generic.List[string]]::new()
try {
    $serial.Open()
    Write-Host "Listening on $Port. Reset the ESP32 if its startup report has already passed."
    $watch = [System.Diagnostics.Stopwatch]::StartNew()
    while ($watch.Elapsed.TotalSeconds -lt $TimeoutSeconds) {
        try { $line = $serial.ReadLine().Trim() }
        catch [System.TimeoutException] { continue }
        if ($line) {
            $receivedLines++
            if ($preview.Count -lt 12) { $preview.Add($line) }
            Write-Verbose "Serial: $line"
        }
        if ($line -eq 'ARC_BUILD_INFO_BEGIN') { $inside = $true; continue }
        if ($line -eq 'ARC_BUILD_INFO_END' -and $inside) { break }
        if ($inside -and $line.StartsWith('{')) { $payload = $line }
    }
    if (-not $payload) {
        $sample = $preview -join ' | '
        throw "No firmware build manifest received in $TimeoutSeconds seconds ($receivedLines serial lines). Sample: $sample. Confirm updated firmware is uploaded, USB CDC On Boot is enabled, and press RESET while listening. Use -Verbose for all received lines."
    }
}
finally { if ($serial.IsOpen) { $serial.Close() }; $serial.Dispose() }
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

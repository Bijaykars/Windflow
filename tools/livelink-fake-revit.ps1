<#
.SYNOPSIS
    Stands in for the WindFlow Revit add-in (the pipe CLIENT, see docs/LIVELINK.md) to test the app's live-link
    server without Revit. Connects to \\.\pipe\WindFlow.LiveLink, sends a full `model` from a JSON file, a
    `ping`, a malformed line and an invalid model (both must come back as acks with an error field), then 5 s
    later an `opening` toggle for the first open window, then `close`. Prints every line the app sends back.

.HOW TO TEST
    1. Export the sample house once:
         src\WindFlow.App\bin\Release\net10.0-windows\win-x64\WindFlow.App.exe --export-sample out\sample.windflow.json
    2. Start the app with the link on (headless bench, or interactively with Advanced > Live link):
         WindFlow.App.exe --bench 30 --live --livelink
    3. Run this script (it retries the connection for -ConnectSeconds):
         powershell -NoProfile -ExecutionPolicy Bypass -File tools\livelink-fake-revit.ps1 -Model out\sample.windflow.json
    4. Read the app log (%LOCALAPPDATA%\WindFlow\logs, newest file): "live link: #n <type> ... applied in N ms".
#>

[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$Model,
    [string]$PipeName = "WindFlow.LiveLink",
    [string]$Doc = "Fake Revit - sample house",
    [int]$WarmupSeconds = 4,
    [int]$ToggleAfterSeconds = 5,
    [int]$ConnectSeconds = 60
)

function Say([string]$Message) { Write-Host "[$( Get-Date -Format 'HH:mm:ss.fff' )] $Message" }

$json = [System.IO.File]::ReadAllText((Resolve-Path $Model))
$parsed = $json | ConvertFrom-Json
$window = $parsed.Openings | Where-Object { $_.Kind -eq 0 -or $_.Kind -eq "Window" } | Where-Object { $_.IsOpen } | Select-Object -First 1
if ($null -eq $window) { $window = $parsed.Openings | Select-Object -First 1 }
Say "model $Model : $($json.Length) chars, $($parsed.Openings.Count) openings; will toggle '$($window.Name)' ($($window.Id)) to isOpen=$(-not $window.IsOpen)"

$pipe = New-Object System.IO.Pipes.NamedPipeClientStream(".", $PipeName, [System.IO.Pipes.PipeDirection]::InOut,
    [System.IO.Pipes.PipeOptions]::Asynchronous)
$deadline = (Get-Date).AddSeconds($ConnectSeconds)
while (-not $pipe.IsConnected) {
    try { $pipe.Connect(1000) } catch { if ((Get-Date) -gt $deadline) { Say "no WindFlow server on \\.\pipe\$PipeName"; exit 1 } }
}
Say "connected"
$utf8 = New-Object System.Text.UTF8Encoding($false)
$reader = New-Object System.IO.StreamReader($pipe, $utf8, $false, 4096, $true)
$writer = New-Object System.IO.StreamWriter($pipe, $utf8, 4096, $true)
$writer.NewLine = "`n"
$writer.AutoFlush = $true

# The app acks every message after applying it, so a blocking read after each send is the round trip.
function Send([string]$Line, [string]$Label) {
    $clock = [System.Diagnostics.Stopwatch]::StartNew()
    $writer.WriteLine($Line)
    $ack = $reader.ReadLine()
    Say ("sent {0,-14} {1,9:N0} B  -> {2}  ({3} ms)" -f $Label, $utf8.GetByteCount($Line), $ack, $clock.ElapsedMilliseconds)
}

function Q([string]$s) { ($s | ConvertTo-Json -Compress) }   # a JSON string literal, escaped

Start-Sleep -Seconds $WarmupSeconds
Send ('{"type":"model","doc":' + (Q $Doc) + ',"json":' + (Q $json) + '}') "model"
Send '{"type":"ping"}' "ping"
Send '{"type":"model","doc":"broken", "json": ' "malformed"
Send ('{"type":"model","doc":"empty","json":' + (Q '{"Name":"empty","Obstacles":[]}') + '}') "invalid model"
Start-Sleep -Seconds $ToggleAfterSeconds
$open = if ($window.IsOpen) { "false" } else { "true" }
Send ('{"type":"opening","id":' + (Q $window.Id) + ',"isOpen":' + $open + '}') "opening"
Send '{"type":"opening","id":"no-such-id","isOpen":true}' "unknown opening"
Start-Sleep -Seconds 2
Send '{"type":"close"}' "close"

# Hang up like Revit does after DocumentClosing.
$writer.Dispose(); $reader.Dispose(); $pipe.Dispose()
Say "disconnected"

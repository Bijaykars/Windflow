<#
.SYNOPSIS
    Stands in for WindFlow.App's pipe server, for testing the Revit "Live link" button without
    building the App-side listener. Creates \\.\pipe\WindFlow.LiveLink, accepts one connection,
    and prints every newline-delimited JSON message it receives. See docs/LIVELINK.md.

.HOW TO TEST
    1. Run this script FIRST (it must own the pipe before Revit tries to connect):
         powershell -File tools/livelink-fake-app.ps1
    2. Open Revit 2026 with the WindFlow add-in loaded, open a project, click "Live link" on the
       WindFlow ribbon panel. The button should read "Live link (connected)" and this script should
       immediately print a `model` line (the whole building, as JSON).
    3. Toggle a window or door open/closed in the Revit model. Within about a second you should see
       either a cheap `opening` line (if the add-in recognised the parameter) or a full `model` line
       (fallback). Either is correct per docs/LIVELINK.md.
    4. Move a wall or add a room and you should see another `model` line after the 1 s debounce.
    5. Every 5 seconds with no other traffic you should see a `ping` line.
    6. Click "Live link" again to turn it off (or close the Revit document) -- you should see a
       `close` line and then this script's connection ends; it goes back to waiting for a new one.
    7. Ctrl+C to stop the script.
#>

[CmdletBinding()]
param(
    [string]$PipeName = "WindFlow.LiveLink"
)

Add-Type -AssemblyName System.Core

function Write-Timestamped([string]$Message) {
    Write-Host "[$( Get-Date -Format 'HH:mm:ss.fff' )] $Message"
}

Write-Timestamped "Fake WindFlow.App pipe server starting on \\.\pipe\$PipeName -- waiting for Revit to connect..."

while ($true) {
    $pipe = New-Object System.IO.Pipes.NamedPipeServerStream(
        $PipeName,
        [System.IO.Pipes.PipeDirection]::InOut,
        1,
        [System.IO.Pipes.PipeTransmissionMode]::Byte,
        [System.IO.Pipes.PipeOptions]::Asynchronous)

    try {
        $pipe.WaitForConnection()
        Write-Timestamped "Revit connected."

        $reader = New-Object System.IO.StreamReader($pipe, [System.Text.Encoding]::UTF8, $false, 4096, $true)
        $writer = New-Object System.IO.StreamWriter($pipe, (New-Object System.Text.UTF8Encoding($false)), 4096, $true)
        $writer.NewLine = "`n"
        $writer.AutoFlush = $true

        while ($pipe.IsConnected) {
            $line = $reader.ReadLine()
            if ($null -eq $line) { break }
            if ($line.Length -eq 0) { continue }

            try {
                $obj = $line | ConvertFrom-Json
                switch ($obj.type) {
                    "model"   { Write-Timestamped "model    doc='$($obj.doc)'  json length=$($obj.json.Length) chars" }
                    "opening" { Write-Timestamped "opening  id='$($obj.id)'  isOpen=$($obj.isOpen)" }
                    "ping"    { Write-Timestamped "ping" }
                    "close"   { Write-Timestamped "close" }
                    default   { Write-Timestamped "unknown message: $line" }
                }
            } catch {
                Write-Timestamped "could not parse line as JSON: $line"
            }
        }

        Write-Timestamped "Revit disconnected. Waiting for a new connection..."
    } catch {
        Write-Timestamped "pipe error: $($_.Exception.Message)"
    } finally {
        $pipe.Dispose()
    }
}

#requires -Version 7.0
[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$CapturePath
)

$ErrorActionPreference = 'Stop'

function Read-TimeMs($Value, [string]$Field) {
    if (($Value -isnot [long] -and $Value -isnot [double]) -or
        ![double]::IsFinite($Value) -or $Value -lt 0) {
        throw "$Field must be a finite, nonnegative number of milliseconds."
    }

    return [double]$Value
}

$document = Get-Content -LiteralPath $CapturePath -Raw | ConvertFrom-Json
if ($document.runs -isnot [array] -or $document.runs.Count -eq 0) {
    throw 'Provide a JSON object containing a nonempty runs array.'
}

foreach ($group in ($document.runs | Group-Object -Property app, load)) {
    $latencies = [System.Collections.Generic.List[double]]::new()
    $missedNotes = 0
    $dropoutCount = 0
    $dropoutMs = 0.0
    $observedDropoutWindowMs = 0.0
    foreach ($run in $group.Group) {
        if ($run.app -notin @('Electron', 'WinUI') -or $run.load -notin @('idle', 'loaded')) {
            throw 'Each run needs app Electron or WinUI and load idle or loaded.'
        }

        $durationMs = Read-TimeMs $run.durationMs 'durationMs'
        if ($durationMs -eq 0) {
            throw 'A capture must have a positive durationMs.'
        }
        if ($run.strikes -isnot [array]) {
            throw 'Each run needs a strikes array. Use [] for a dropout-only capture.'
        }

        $lastPressMs = -1.0
        foreach ($strike in $run.strikes) {
            $pressMs = Read-TimeMs $strike.pressMs 'strike pressMs'
            if ($pressMs -le $lastPressMs -or $pressMs -ge $durationMs) {
                throw 'Strike markers must be in strictly increasing order and inside the capture.'
            }
            $lastPressMs = $pressMs

            if ('soundMs' -notin $strike.PSObject.Properties.Name) {
                throw 'Set soundMs to null explicitly for a press without a detected note.'
            }
            if ($null -eq $strike.soundMs) {
                $missedNotes++
                continue
            }

            $soundMs = Read-TimeMs $strike.soundMs 'strike soundMs'
            if ($soundMs -lt $pressMs -or $soundMs -ge $durationMs) {
                throw 'A sound marker must follow its press and be inside the capture.'
            }
            $latencies.Add($soundMs - $pressMs)
        }

        if ('dropouts' -notin $run.PSObject.Properties.Name) {
            throw 'Each run must declare dropouts explicitly, using null for an unscored capture.'
        }
        if ($null -eq $run.dropouts) {
            continue
        }
        if ($run.dropouts -isnot [array]) {
            throw 'Use a dropouts array for a scored capture or null for an unscored capture.'
        }

        $observedDropoutWindowMs += $durationMs
        $lastEndMs = 0.0
        foreach ($dropout in $run.dropouts) {
            $startMs = Read-TimeMs $dropout.startMs 'dropout startMs'
            $endMs = Read-TimeMs $dropout.endMs 'dropout endMs'
            if ($startMs -lt $lastEndMs -or $endMs -le $startMs -or $endMs -gt $durationMs) {
                throw 'Dropout intervals must be ordered, disjoint, and inside the capture.'
            }
            $lastEndMs = $endMs
            $dropoutCount++
            $dropoutMs += $endMs - $startMs
        }
    }

    $medianMs = $null
    $p95Ms = $null
    if ($latencies.Count -gt 0) {
        $latencies.Sort()
        $middle = [int][math]::Floor($latencies.Count / 2)
        $medianMs = $latencies[$middle]
        if ($latencies.Count % 2 -eq 0) {
            $medianMs = ($latencies[$middle - 1] + $medianMs) / 2
        }
        $p95Ms = $latencies[[int][math]::Ceiling(0.95 * $latencies.Count) - 1]
    }

    [pscustomobject]@{
        App = $group.Group[0].app
        Load = $group.Group[0].load
        NotesDetected = $latencies.Count
        NotesMissed = $missedNotes
        MedianMs = $medianMs
        P95Ms = $p95Ms
        CaptureMinutes = if ($observedDropoutWindowMs -gt 0) { $observedDropoutWindowMs / 60000 }
        DropoutCount = if ($observedDropoutWindowMs -gt 0) { $dropoutCount }
        DropoutMs = if ($observedDropoutWindowMs -gt 0) { $dropoutMs }
        DropoutsPerMinute = if ($observedDropoutWindowMs -gt 0) { $dropoutCount * 60000 / $observedDropoutWindowMs }
    }
}

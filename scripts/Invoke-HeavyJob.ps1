[CmdletBinding()]
param(
    [Parameter(Mandatory, Position = 0)]
    [string] $Label,

    [Parameter(Mandatory, Position = 1)]
    [scriptblock] $Job,

    [ValidateRange(1, 1440)]
    [int] $TimeoutMinutes = 240
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$protocolVersion = 1
$pipeName = "GitHubCopilot.HeavyJobQueue.v1"
$requestId = [Guid]::NewGuid()
$pipe = $null
$reader = $null
$writer = $null

function Read-BrokerMessage {
    param(
        [Parameter(Mandatory)]
        [TimeSpan] $Timeout
    )

    $readTask = $reader.ReadLineAsync()
    if (-not $readTask.Wait($Timeout)) {
        throw "Timed out waiting for a response from the Heavy Job Queue broker."
    }

    $line = $readTask.GetAwaiter().GetResult()
    if ($null -eq $line) {
        throw "The Heavy Job Queue broker disconnected unexpectedly."
    }

    try {
        return $line | ConvertFrom-Json
    }
    catch {
        throw "The Heavy Job Queue broker returned malformed JSON: $line"
    }
}

function Assert-BrokerMessage {
    param(
        [Parameter(Mandatory)]
        [object] $Message
    )

    if ($null -eq $Message.PSObject.Properties["version"] -or
        $Message.version -ne $protocolVersion) {
        throw "The Heavy Job Queue broker returned an incompatible protocol version."
    }

    if ($null -eq $Message.PSObject.Properties["type"]) {
        throw "The Heavy Job Queue broker returned a message without a type."
    }

    if ($Message.type -eq "error") {
        $code = if ($null -ne $Message.PSObject.Properties["code"]) {
            $Message.code
        } else {
            "unknown_error"
        }
        $message = if ($null -ne $Message.PSObject.Properties["message"]) {
            $Message.message
        } else {
            "No details were provided."
        }
        throw "Heavy Job Queue broker error [$code]: $message"
    }
}

try {
    $pipe = [IO.Pipes.NamedPipeClientStream]::new(
        ".",
        $pipeName,
        [IO.Pipes.PipeDirection]::InOut,
        [IO.Pipes.PipeOptions]::Asynchronous
    )

    try {
        $pipe.Connect(5000)
    }
    catch {
        throw "Heavy Job Queue broker is unavailable. Start HeavyJobQueue.exe and retry. $($_.Exception.Message)"
    }

    $reader = [IO.StreamReader]::new(
        $pipe,
        [Text.UTF8Encoding]::new($false, $true),
        $false,
        1024,
        $true
    )
    $writer = [IO.StreamWriter]::new(
        $pipe,
        [Text.UTF8Encoding]::new($false),
        1024,
        $true
    )
    $writer.NewLine = "`n"
    $writer.AutoFlush = $true

    $enqueueTime = [DateTimeOffset]::UtcNow
    $deadline = $enqueueTime.AddMinutes($TimeoutMinutes)
    $enqueue = [ordered] @{
        version = $protocolVersion
        type = "enqueue"
        requestId = $requestId.ToString("D")
        label = $Label
        callerPid = $PID
        cwd = (Get-Location).ProviderPath
        command = $Job.ToString()
        enqueuedAt = $enqueueTime.ToString("o")
        waitTimeoutSeconds = $TimeoutMinutes * 60
    }
    $writer.WriteLine(($enqueue | ConvertTo-Json -Compress))

    $granted = $false
    $isPaused = $false
    $pausedAt = $null
    while (-not $granted) {
        $remaining = $deadline - [DateTimeOffset]::UtcNow
        if (-not $isPaused -and $remaining -le [TimeSpan]::Zero) {
            $cancel = [ordered] @{
                version = $protocolVersion
                type = "cancel"
                requestId = $requestId.ToString("D")
                reason = "wait_timeout"
            }
            $writer.WriteLine(($cancel | ConvertTo-Json -Compress))
            throw "Timed out waiting for the Heavy Job Queue grant."
        }

        $readTimeout = if ($isPaused) {
            [Threading.Timeout]::InfiniteTimeSpan
        } else {
            $remaining
        }
        $message = Read-BrokerMessage -Timeout $readTimeout
        Assert-BrokerMessage -Message $message

        switch ($message.type) {
            "queued" {
                $position = if ($null -ne $message.PSObject.Properties["position"]) {
                    $message.position
                } else {
                    "?"
                }
                Write-Host "Heavy job queued at position ${position}: $Label"
            }
            "paused" {
                if ($message.requestId -ne $requestId.ToString("D")) {
                    throw "The Heavy Job Queue broker paused a different request."
                }
                if (-not $isPaused) {
                    $isPaused = $true
                    $pausedAt = [DateTimeOffset]::UtcNow
                    Write-Host "Heavy job paused by the queue operator: $Label"
                }
            }
            "resumed" {
                if ($message.requestId -ne $requestId.ToString("D")) {
                    throw "The Heavy Job Queue broker resumed a different request."
                }
                if ($isPaused) {
                    $deadline = $deadline.Add([DateTimeOffset]::UtcNow - $pausedAt)
                    $isPaused = $false
                    $pausedAt = $null
                    Write-Host "Heavy job resumed in the queue: $Label"
                }
            }
            "grant" {
                if ($message.requestId -ne $requestId.ToString("D")) {
                    throw "The Heavy Job Queue broker granted a different request."
                }
                $granted = $true
            }
            default {
                throw "Unexpected Heavy Job Queue broker message: $($message.type)"
            }
        }
    }

    Write-Host "Heavy-job slot granted: $Label"

    $executionError = $null
    $jobExitCode = 0
    try {
        $global:LASTEXITCODE = 0
        & $Job
        $jobSucceeded = $?
        $jobExitCode = $global:LASTEXITCODE

        if (-not $jobSucceeded -or $jobExitCode -ne 0) {
            throw "Heavy job failed with exit code ${jobExitCode}: $Label"
        }
    }
    catch {
        $executionError = $_
        if ($jobExitCode -eq 0 -and $global:LASTEXITCODE -ne 0) {
            $jobExitCode = $global:LASTEXITCODE
        }
    }

    $completion = [ordered] @{
        version = $protocolVersion
        type = "complete"
        requestId = $requestId.ToString("D")
        succeeded = $null -eq $executionError
        exitCode = $jobExitCode
        error = if ($null -eq $executionError) {
            $null
        } else {
            $executionError.Exception.Message.Substring(
                0,
                [Math]::Min(4000, $executionError.Exception.Message.Length)
            )
        }
    }

    $reportError = $null
    try {
        $writer.WriteLine(($completion | ConvertTo-Json -Compress))
        $ack = Read-BrokerMessage -Timeout ([TimeSpan]::FromSeconds(10))
        Assert-BrokerMessage -Message $ack
        if ($ack.type -ne "ack" -or $ack.requestId -ne $requestId.ToString("D")) {
            throw "The Heavy Job Queue broker returned an invalid completion acknowledgement."
        }
    }
    catch {
        $reportError = $_
    }

    if ($null -ne $executionError) {
        if ($null -ne $reportError) {
            throw "$($executionError.Exception.Message) The broker also failed to record completion: $($reportError.Exception.Message)"
        }
        throw $executionError
    }

    if ($null -ne $reportError) {
        throw "The job succeeded, but the broker failed to record completion: $($reportError.Exception.Message)"
    }

    Write-Host "Heavy-job slot released: $Label"
}
finally {
    if ($null -ne $writer) {
        $writer.Dispose()
    }
    if ($null -ne $reader) {
        $reader.Dispose()
    }
    if ($null -ne $pipe) {
        $pipe.Dispose()
    }
}

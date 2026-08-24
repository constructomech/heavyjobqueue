[CmdletBinding()]
param(
    [Parameter(Mandatory, Position = 0)]
    [string] $Label,

    [Parameter(Mandatory, Position = 1)]
    [scriptblock] $Job,

    [ValidateRange(1, 1440)]
    [int] $TimeoutMinutes = 240,

    [ValidateRange(1, 1440)]
    [int] $PauseTimeoutMinutes = 60,

    [ValidateRange(5, 3600)]
    [int] $HeartbeatSeconds = 60
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$protocolVersion = 2
$pipeName = "GitHubCopilot.HeavyJobQueue.v2"
$requestId = [Guid]::NewGuid()
$leaseName = "Local\GitHubCopilot.HeavyJobQueue.Lease.$($requestId.ToString('D'))"
$leaseMutex = [Threading.Mutex]::new($false, $leaseName)
$pipe = $null
$reader = $null
$writer = $null
$pendingRead = $null
$maxHeartbeatInterval = [TimeSpan]::FromMinutes(15)

function Close-BrokerConnection {
    $script:pendingRead = $null
    if ($null -ne $writer) {
        try {
            $writer.Dispose()
        }
        catch [IO.IOException] {
        }
        $script:writer = $null
    }
    if ($null -ne $reader) {
        try {
            $reader.Dispose()
        }
        catch [IO.IOException] {
        }
        $script:reader = $null
    }
    if ($null -ne $pipe) {
        try {
            $pipe.Dispose()
        }
        catch [IO.IOException] {
        }
        $script:pipe = $null
    }
}

function Connect-Broker {
    param(
        [int] $TimeoutMilliseconds = 2000
    )

    Close-BrokerConnection
    $newPipe = [IO.Pipes.NamedPipeClientStream]::new(
        ".",
        $pipeName,
        [IO.Pipes.PipeDirection]::InOut,
        [IO.Pipes.PipeOptions]::Asynchronous
    )

    try {
        $newPipe.Connect($TimeoutMilliseconds)
        $script:pipe = $newPipe
        $script:reader = [IO.StreamReader]::new(
            $pipe,
            [Text.UTF8Encoding]::new($false, $true),
            $false,
            1024,
            $true
        )
        $script:writer = [IO.StreamWriter]::new(
            $pipe,
            [Text.UTF8Encoding]::new($false),
            1024,
            $true
        )
        $writer.NewLine = "`n"
        $writer.AutoFlush = $true
    }
    catch {
        $newPipe.Dispose()
        Close-BrokerConnection
        throw
    }
}

function Resolve-TaskException {
    param(
        [Parameter(Mandatory)]
        [Management.Automation.ErrorRecord] $ErrorRecord
    )

    $exception = $ErrorRecord.Exception
    while ($null -ne $exception.InnerException -and (
            $exception -is [Management.Automation.MethodInvocationException] -or
            $exception -is [AggregateException])) {
        $exception = $exception.InnerException
    }

    return $exception
}

function Read-BrokerMessage {
    param(
        [Parameter(Mandatory)]
        [TimeSpan] $Timeout
    )

    # The pending read is kept across timeouts because StreamReader rejects a
    # second ReadLineAsync while an earlier one is still outstanding.
    if ($null -eq $pendingRead) {
        $script:pendingRead = $reader.ReadLineAsync()
    }

    $readTask = $pendingRead
    try {
        $completed = $readTask.Wait($Timeout)
    }
    catch {
        $script:pendingRead = $null
        throw (Resolve-TaskException -ErrorRecord $_)
    }

    if (-not $completed) {
        throw [TimeoutException]::new(
            "Timed out waiting for a response from the Heavy Job Queue broker.")
    }

    $script:pendingRead = $null

    try {
        $line = $readTask.GetAwaiter().GetResult()
    }
    catch {
        throw (Resolve-TaskException -ErrorRecord $_)
    }

    if ($null -eq $line) {
        throw [IO.EndOfStreamException]::new(
            "The Heavy Job Queue broker disconnected.")
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

function Format-Duration {
    param(
        [Parameter(Mandatory)]
        [TimeSpan] $Value
    )

    if ($Value -lt [TimeSpan]::Zero) {
        $Value = [TimeSpan]::Zero
    }

    if ($Value.Days -gt 0) {
        return $Value.ToString("d\d\ hh\:mm\:ss")
    }

    return $Value.ToString("hh\:mm\:ss")
}

function Get-WaitLimit {
    if ($isPaused) {
        return $pauseDeadline
    }

    return $deadline
}

function Write-WaitHeartbeat {
    $now = [DateTimeOffset]::UtcNow
    $waited = Format-Duration -Value ($now - $enqueueTime)
    $left = Format-Duration -Value ((Get-WaitLimit) - $now)
    if ($isPaused) {
        Write-Host ("Heavy job still paused by the queue operator after ${waited}; " +
            "cancels in ${left}: $Label")
    }
    else {
        Write-Host ("Heavy job still waiting after ${waited} at queue position " +
            "${lastPosition}; gives up in ${left}: $Label")
    }
}

function Send-Enqueue {
    $writer.WriteLine(($enqueue | ConvertTo-Json -Compress))
}

function Reconnect-Waiting {
    while ([DateTimeOffset]::UtcNow -lt (Get-WaitLimit)) {
        try {
            Connect-Broker
            Send-Enqueue
            return
        }
        catch {
            Close-BrokerConnection
            Start-Sleep -Milliseconds 500
        }
    }

    throw "Timed out while reconnecting to the Heavy Job Queue broker."
}

function Send-Cancel {
    $cancel = [ordered] @{
        version = $protocolVersion
        type = "cancel"
        requestId = $requestId.ToString("D")
        leaseName = $leaseName
        reason = "wait_timeout"
    }

    # Best effort only. The broker independently reaps the entry when its own
    # wait timeout fires or when this wrapper's lease ends, and a failed cancel
    # must not mask the timeout that caused it.
    try {
        if ($null -ne $writer) {
            $writer.WriteLine(($cancel | ConvertTo-Json -Compress))
        }
    }
    catch {
    }
}

if (-not $leaseMutex.WaitOne(0)) {
    $leaseMutex.Dispose()
    throw "Could not acquire the unique heavy-job request lease."
}

try {
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
        leaseName = $leaseName
    }

    try {
        Connect-Broker -TimeoutMilliseconds 5000
        Send-Enqueue
    }
    catch {
        throw "Heavy Job Queue broker is unavailable. Start HeavyJobQueue.exe and retry. $($_.Exception.Message)"
    }

    $granted = $false
    $isPaused = $false
    $pausedAt = $null
    $pauseDeadline = $deadline
    $lastPosition = "?"
    $heartbeatInterval = [TimeSpan]::FromSeconds($HeartbeatSeconds)
    $reportedReconnect = $false
    while (-not $granted) {
        $remaining = (Get-WaitLimit) - [DateTimeOffset]::UtcNow
        if ($remaining -le [TimeSpan]::Zero) {
            Send-Cancel
            if ($isPaused) {
                throw ("Timed out after $PauseTimeoutMinutes minute(s) while the Heavy " +
                    "Job Queue held this job paused: $Label")
            }

            throw "Timed out waiting for the Heavy Job Queue grant."
        }

        $readTimeout = if ($remaining -lt $heartbeatInterval) {
            $remaining
        } else {
            $heartbeatInterval
        }

        try {
            $message = Read-BrokerMessage -Timeout $readTimeout
            Assert-BrokerMessage -Message $message
        }
        catch [TimeoutException] {
            if (((Get-WaitLimit) - [DateTimeOffset]::UtcNow) -gt [TimeSpan]::Zero) {
                Write-WaitHeartbeat
                $doubled = $heartbeatInterval + $heartbeatInterval
                $heartbeatInterval = if ($doubled -gt $maxHeartbeatInterval) {
                    $maxHeartbeatInterval
                } else {
                    $doubled
                }
            }

            continue
        }
        catch [IO.IOException] {
            if (-not $reportedReconnect) {
                Write-Host "Heavy Job Queue broker restarted; reclaiming queued job: $Label"
                $reportedReconnect = $true
            }
            Reconnect-Waiting
            continue
        }
        catch [ObjectDisposedException] {
            Reconnect-Waiting
            continue
        }

        switch ($message.type) {
            "queued" {
                $position = if ($null -ne $message.PSObject.Properties["position"]) {
                    $message.position
                } else {
                    "?"
                }
                $lastPosition = $position
                Write-Host "Heavy job queued at position ${position}: $Label"
            }
            "paused" {
                if ($message.requestId -ne $requestId.ToString("D")) {
                    throw "The Heavy Job Queue broker paused a different request."
                }
                if (-not $isPaused) {
                    $isPaused = $true
                    $pausedAt = [DateTimeOffset]::UtcNow
                    $pauseDeadline = $pausedAt.AddMinutes($PauseTimeoutMinutes)
                    $heartbeatInterval = [TimeSpan]::FromSeconds($HeartbeatSeconds)
                    Write-Host ("Heavy job paused by the queue operator; cancels after " +
                        "$PauseTimeoutMinutes minute(s): $Label")
                }
            }
            "resumed" {
                if ($message.requestId -ne $requestId.ToString("D")) {
                    throw "The Heavy Job Queue broker resumed a different request."
                }
                if ($isPaused) {
                    $deadline = $deadline.Add([DateTimeOffset]::UtcNow - $pausedAt)
                    $pauseDeadline = $deadline
                    $isPaused = $false
                    $pausedAt = $null
                    $heartbeatInterval = [TimeSpan]::FromSeconds($HeartbeatSeconds)
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
        leaseName = $leaseName
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
    $reportDeadline = [DateTimeOffset]::UtcNow.AddMinutes(5)
    while ([DateTimeOffset]::UtcNow -lt $reportDeadline) {
        try {
            if ($null -eq $writer) {
                Connect-Broker
            }
            $writer.WriteLine(($completion | ConvertTo-Json -Compress))
            $ack = Read-BrokerMessage -Timeout ([TimeSpan]::FromSeconds(10))
            Assert-BrokerMessage -Message $ack
            if ($ack.type -ne "ack" -or $ack.requestId -ne $requestId.ToString("D")) {
                throw "The Heavy Job Queue broker returned an invalid completion acknowledgement."
            }
            $reportError = $null
            break
        }
        catch {
            $reportError = $_
            Close-BrokerConnection
            Start-Sleep -Milliseconds 500
        }
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
    Close-BrokerConnection
    $leaseMutex.ReleaseMutex()
    $leaseMutex.Dispose()
}

[CmdletBinding()]
param(
    [switch] $EnableStartup,
    [switch] $DisableStartup,
    [switch] $NoLaunch,
    [switch] $SkipInstructions
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

if ($EnableStartup -and $DisableStartup) {
    throw "EnableStartup and DisableStartup cannot be used together."
}

$repositoryRoot = Split-Path -Parent $PSScriptRoot
$projectPath = Join-Path $repositoryRoot "src\HeavyJobQueue.App\HeavyJobQueue.App.csproj"
$sourceWrapper = Join-Path $PSScriptRoot "Invoke-HeavyJob.ps1"
$sourceInstructions = Join-Path $repositoryRoot "instructions\heavy-job-queue.instructions.md"
$toolsDirectory = Join-Path $HOME ".copilot\tools"
$installDirectory = Join-Path $toolsDirectory "HeavyJobQueue"
$legacyInstallDirectory = Join-Path $env:LOCALAPPDATA "GitHubCopilot\HeavyJobQueue"
$instructionsDirectory = Join-Path $HOME ".copilot\instructions"
$installedWrapper = Join-Path $toolsDirectory "Invoke-HeavyJob.ps1"
$installedInstructions = Join-Path $instructionsDirectory "heavy-job-queue.instructions.md"
$executablePath = Join-Path $installDirectory "HeavyJobQueue.exe"
$obsoleteSingleFilePath = Join-Path $toolsDirectory "HeavyJobQueue.exe"
$legacyExecutablePath = Join-Path $legacyInstallDirectory "HeavyJobQueue.exe"
$legacyLockDirectory = Join-Path $env:LOCALAPPDATA "GitHubCopilot\locks"
$legacyLockPath = Join-Path $legacyLockDirectory "heavy-job.lock"
$legacyOwnerPath = Join-Path $legacyLockDirectory "heavy-job.owner.json"
$runKeyPath = "HKCU:\Software\Microsoft\Windows\CurrentVersion\Run"
$runValueName = "GitHubCopilotHeavyJobQueue"
$transactionId = [Guid]::NewGuid().ToString("N")
$publishDirectory = Join-Path ([IO.Path]::GetTempPath()) "HeavyJobQueue-$transactionId"
$stagingInstallDirectory = Join-Path $toolsDirectory ".HeavyJobQueue.install-$transactionId"
$backupInstallDirectory = Join-Path $toolsDirectory ".HeavyJobQueue.backup-$transactionId"

if (-not $SkipInstructions -and (Test-Path -LiteralPath $installedInstructions)) {
    $existingInstructions = Get-Content -LiteralPath $installedInstructions -Raw
    if ($existingInstructions -notmatch '<!-- managed-by: heavyjobqueue -->') {
        throw "Refusing to overwrite unmanaged instructions at '$installedInstructions'. Move that file or use -SkipInstructions."
    }
}

$runningInstances = @(
    Get-Process -Name "HeavyJobQueue" -ErrorAction SilentlyContinue |
        Where-Object {
            $_.Path -in @(
                $executablePath,
                $obsoleteSingleFilePath,
                $legacyExecutablePath
            )
        }
)
if ($runningInstances.Count -gt 0) {
    throw "Exit Heavy Job Queue from its tray menu before installing or upgrading."
}

try {
    [IO.Directory]::CreateDirectory($publishDirectory) | Out-Null

    $publish = {
        dotnet publish $projectPath `
            --configuration Release `
            --runtime win-x64 `
            --self-contained false `
            -p:DebugType=None `
            -p:DebugSymbols=false `
            --output $publishDirectory
        if ($LASTEXITCODE -ne 0) {
            throw "dotnet publish failed with exit code $LASTEXITCODE."
        }
    }

    & $publish

    [IO.Directory]::CreateDirectory($toolsDirectory) | Out-Null
    [IO.Directory]::CreateDirectory($stagingInstallDirectory) | Out-Null
    Copy-Item `
        -Path (Join-Path $publishDirectory "*") `
        -Destination $stagingInstallDirectory `
        -Recurse `
        -Force
    if (-not (Test-Path -LiteralPath (Join-Path $stagingInstallDirectory "HeavyJobQueue.exe"))) {
        throw "Published application is missing HeavyJobQueue.exe."
    }

    if (Test-Path -LiteralPath $installDirectory) {
        Move-Item `
            -LiteralPath $installDirectory `
            -Destination $backupInstallDirectory
    }
    Move-Item `
        -LiteralPath $stagingInstallDirectory `
        -Destination $installDirectory
    if (Test-Path -LiteralPath $backupInstallDirectory) {
        Remove-Item -LiteralPath $backupInstallDirectory -Recurse -Force
    }

    Copy-Item -LiteralPath $sourceWrapper -Destination $installedWrapper -Force

    if (Test-Path -LiteralPath $obsoleteSingleFilePath) {
        Remove-Item -LiteralPath $obsoleteSingleFilePath -Force
    }
    if (Test-Path -LiteralPath $legacyExecutablePath) {
        Remove-Item -LiteralPath $legacyInstallDirectory -Recurse -Force
    }
    Remove-Item -LiteralPath $legacyLockPath -Force -ErrorAction SilentlyContinue
    Remove-Item -LiteralPath $legacyOwnerPath -Force -ErrorAction SilentlyContinue

    if (-not $SkipInstructions) {
        [IO.Directory]::CreateDirectory($instructionsDirectory) | Out-Null
        Copy-Item -LiteralPath $sourceInstructions -Destination $installedInstructions -Force
        Write-Host "Installed Copilot instructions to: $installedInstructions"
    }

    if (-not $DisableStartup) {
        if (-not (Test-Path -LiteralPath $runKeyPath)) {
            New-Item -Path $runKeyPath -Force | Out-Null
        }
        New-ItemProperty `
            -Path $runKeyPath `
            -Name $runValueName `
            -Value "`"$executablePath`"" `
            -PropertyType String `
            -Force | Out-Null
        Write-Host "Enabled per-user startup."
    }
    else {
        Remove-ItemProperty `
            -Path $runKeyPath `
            -Name $runValueName `
            -ErrorAction SilentlyContinue
        Write-Host "Disabled per-user startup."
    }

    if (-not $NoLaunch) {
        Start-Process -FilePath $executablePath
    }

    Write-Host "Installed Heavy Job Queue to: $executablePath"
    Write-Host "Installed wrapper to: $installedWrapper"
    if (-not $SkipInstructions) {
        Write-Host "Restart active Copilot sessions to load the new instructions."
    }
}
finally {
    if (Test-Path -LiteralPath $backupInstallDirectory) {
        if (Test-Path -LiteralPath $installDirectory) {
            Remove-Item -LiteralPath $backupInstallDirectory -Recurse -Force
        }
        else {
            Move-Item `
                -LiteralPath $backupInstallDirectory `
                -Destination $installDirectory
        }
    }
    if (Test-Path -LiteralPath $stagingInstallDirectory) {
        Remove-Item -LiteralPath $stagingInstallDirectory -Recurse -Force
    }
    if (Test-Path -LiteralPath $publishDirectory) {
        Remove-Item -LiteralPath $publishDirectory -Recurse -Force
    }
}

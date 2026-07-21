[CmdletBinding()]
param(
    [switch] $EnableStartup,
    [switch] $DisableStartup,
    [switch] $NoLaunch
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

if ($EnableStartup -and $DisableStartup) {
    throw "EnableStartup and DisableStartup cannot be used together."
}

$repositoryRoot = Split-Path -Parent $PSScriptRoot
$projectPath = Join-Path $repositoryRoot "src\HeavyJobQueue.App\HeavyJobQueue.App.csproj"
$sourceWrapper = Join-Path $PSScriptRoot "Invoke-HeavyJob.ps1"
$installDirectory = Join-Path $env:LOCALAPPDATA "GitHubCopilot\HeavyJobQueue"
$toolsDirectory = Join-Path $HOME ".copilot\tools"
$installedWrapper = Join-Path $toolsDirectory "Invoke-HeavyJob.ps1"
$executablePath = Join-Path $installDirectory "HeavyJobQueue.exe"
$runKeyPath = "HKCU:\Software\Microsoft\Windows\CurrentVersion\Run"
$runValueName = "GitHubCopilotHeavyJobQueue"
$publishDirectory = Join-Path ([IO.Path]::GetTempPath()) "HeavyJobQueue-$([Guid]::NewGuid().ToString('N'))"

try {
    [IO.Directory]::CreateDirectory($publishDirectory) | Out-Null

    $publish = {
        dotnet publish $projectPath `
            --configuration Release `
            --runtime win-x64 `
            --self-contained false `
            --output $publishDirectory
        if ($LASTEXITCODE -ne 0) {
            throw "dotnet publish failed with exit code $LASTEXITCODE."
        }
    }

    & $publish

    [IO.Directory]::CreateDirectory($installDirectory) | Out-Null
    [IO.Directory]::CreateDirectory($toolsDirectory) | Out-Null
    Copy-Item -Path (Join-Path $publishDirectory "*") -Destination $installDirectory -Recurse -Force
    Copy-Item -LiteralPath $sourceWrapper -Destination $installedWrapper -Force

    if ($EnableStartup) {
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
    elseif ($DisableStartup) {
        Remove-ItemProperty `
            -Path $runKeyPath `
            -Name $runValueName `
            -ErrorAction SilentlyContinue
        Write-Host "Disabled per-user startup."
    }

    if (-not $NoLaunch) {
        Start-Process -FilePath $executablePath
    }

    Write-Host "Installed Heavy Job Queue to: $installDirectory"
    Write-Host "Installed wrapper to: $installedWrapper"
}
finally {
    if (Test-Path -LiteralPath $publishDirectory) {
        Remove-Item -LiteralPath $publishDirectory -Recurse -Force
    }
}

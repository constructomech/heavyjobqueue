# Heavy Job Queue

Heavy Job Queue is a per-user Windows tray application that serializes expensive
developer jobs across terminals and Copilot sessions. It replaces an invisible
file-lock wait with a visible FIFO queue whose waiting jobs can be reordered.

## How it works

- `HeavyJobQueue.exe` is the single-instance queue broker and WPF tray UI.
- `scripts\Invoke-HeavyJob.ps1` submits a job over a current-user-only named
  pipe using a versioned newline-delimited JSON protocol.
- The wrapper stays connected while queued. Once granted, it executes the
  scriptblock in the **calling PowerShell process**, preserving the caller's
  current directory, environment, functions, and toolchain setup.
- A disconnected waiting client is removed automatically. A disconnected active
  client releases the slot.
- Before granting a job, the broker exclusively opens
  `%LOCALAPPDATA%\GitHubCopilot\locks\heavy-job.lock` and writes compatible
  `heavy-job.owner.json` metadata. This prevents overlap with legacy wrappers
  during rollout.

The broker never bypasses the named pipe or legacy lock. If the broker is not
running, the wrapper exits with a clear error.

## Requirements

- Windows 10 or later
- .NET 10 Desktop Runtime for the installed framework-dependent application
- .NET 10 SDK when building or installing from source

## Build and test

On machines with the shared heavy-job convention, serialize build and test:

```powershell
& "$HOME\.copilot\tools\Invoke-HeavyJob.ps1" "Build Heavy Job Queue" {
    dotnet build .\HeavyJobQueue.sln --configuration Release
}

& "$HOME\.copilot\tools\Invoke-HeavyJob.ps1" "Test Heavy Job Queue" {
    dotnet test .\HeavyJobQueue.sln --configuration Release --no-build
}
```

## Install

Close an already installed tray instance before upgrading, then run the explicit
installer from the repository:

```powershell
.\scripts\Install-HeavyJobQueue.ps1
```

This publishes the application to
`%LOCALAPPDATA%\GitHubCopilot\HeavyJobQueue`, installs the replacement wrapper
to `$HOME\.copilot\tools\Invoke-HeavyJob.ps1`, and launches the tray app.

Startup is opt-in and only changed when requested:

```powershell
.\scripts\Install-HeavyJobQueue.ps1 -EnableStartup
.\scripts\Install-HeavyJobQueue.ps1 -DisableStartup
```

Use `-NoLaunch` to install without starting the application. The installer uses
an isolated temporary publish directory and does not silently configure startup.

## Use

The invocation contract remains unchanged:

```powershell
& "$HOME\.copilot\tools\Invoke-HeavyJob.ps1" "Configure CMake" {
    cmake -S . -B build
}
```

The optional third argument is the queue wait timeout in minutes and defaults to
240:

```powershell
& "$HOME\.copilot\tools\Invoke-HeavyJob.ps1" "Run benchmarks" {
    .\build\benchmarks.exe
} -TimeoutMinutes 30
```

The tray menu opens the queue window or exits the broker. The window shows the
active job and all waiters with label, process ID, working directory, and
elapsed time. Select a waiting row and use **Move up** or **Move down**. The
active job cannot be reordered.

## Protocol and security

Protocol v1 uses the named pipe `GitHubCopilot.HeavyJobQueue.v1` with
`PipeOptions.CurrentUserOnly`. Each UTF-8 line is one JSON object. Clients send:

1. `enqueue` with a stable request ID, label, PID, current directory, enqueue
   timestamp, and wait timeout.
2. `complete` after the caller-process scriptblock finishes, or `cancel` while
   waiting.

The broker responds with `queued`, `grant`, `ack`, or an explicit `error`.
Malformed messages, unsupported versions, invalid fields, and unexpected state
transitions are rejected.

## Troubleshooting

**Broker unavailable**: Start
`%LOCALAPPDATA%\GitHubCopilot\HeavyJobQueue\HeavyJobQueue.exe` and retry.

**A job remains blocked by a legacy process**: Inspect
`%LOCALAPPDATA%\GitHubCopilot\locks\heavy-job.owner.json`. The broker waits for
the legacy exclusive lock; it does not bypass it.

**Upgrade cannot replace files**: Exit Heavy Job Queue from its tray menu, then
rerun the installer.

**Queue timeout**: Increase `-TimeoutMinutes`. Closing or interrupting the
wrapper disconnects it and removes its queue entry.

## License

[MIT](LICENSE)

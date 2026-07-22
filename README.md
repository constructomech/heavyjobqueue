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
to `$HOME\.copilot\tools\Invoke-HeavyJob.ps1`, installs modular instructions at
`$HOME\.copilot\instructions\heavy-job-queue.instructions.md`, and launches the
tray app. The installer does not edit or replace
`$HOME\.copilot\copilot-instructions.md`.

Startup is opt-in and only changed when requested:

```powershell
.\scripts\Install-HeavyJobQueue.ps1 -EnableStartup
.\scripts\Install-HeavyJobQueue.ps1 -DisableStartup
```

Use `-NoLaunch` to install without starting the application. The installer uses
an isolated temporary publish directory and does not silently configure startup.
Use `-SkipInstructions` if you only want the application and wrapper. Existing
instructions at the managed path are updated only when they contain Heavy Job
Queue's ownership marker; an unrelated file is never overwritten.

Copilot CLI loads modular user instructions from
`$HOME\.copilot\instructions\**\*.instructions.md`. Restart active sessions
after installation, then use `/instructions` to confirm or disable the file.
See GitHub's
[custom instructions documentation](https://docs.github.com/en/copilot/how-tos/copilot-cli/customize-copilot/add-custom-instructions).

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

The tray menu opens the queue window or exits the broker. The window shows all
active jobs and waiters with label, process ID, working directory, and elapsed
time. Select a waiting row and use **Move up** or **Move down**.

**Run now** is an explicit manual override for times when you judge that the
machine can handle concurrent work. After confirmation, the selected waiter is
granted immediately, even when another broker or legacy job is active. You may
approve multiple overrides. Automatic FIFO grants remain blocked until every
active and overridden job finishes. The broker acquires or retains the legacy
lock as a barrier when possible, but an override intentionally does not wait for
an external legacy lock holder.

**Pause / resume** moves a selected waiter to or from a paused section at the
bottom of the queue. New jobs and unpaused waiters pass paused jobs. Resuming
appends the job behind current waiters but ahead of jobs that remain paused.
Time spent paused does not count against the wrapper's queue wait timeout.

Hover over an active, waiting, or paused row to see the complete PowerShell
scriptblock text submitted by current wrappers. Older protocol-v1 wrappers that
did not send command metadata remain compatible and show a placeholder.

The queue window also includes Task Manager-style 60-second utilization history:
one graph per logical processor plus physical-memory usage. Sampling uses native
Windows system information APIs and does not require performance-counter
packages or an elevated process.

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

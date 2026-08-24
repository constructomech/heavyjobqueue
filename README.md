# Heavy Job Queue

Heavy Job Queue is a per-user Windows tray application that serializes expensive
developer jobs across terminals and Copilot sessions. It replaces an invisible
wait with a visible FIFO queue whose waiting jobs can be reordered.

## How it works

- `HeavyJobQueue.exe` is the single-instance queue broker and WPF tray UI.
- `scripts\Invoke-HeavyJob.ps1` submits a job over a current-user-only named
  pipe using a versioned newline-delimited JSON protocol.
- The wrapper owns a per-request named lease and stays connected while queued.
  Once granted, it executes the
  scriptblock in the **calling PowerShell process**, preserving the caller's
  current directory, environment, functions, and toolchain setup.
- Queue order, pause state (per job and queue-wide), active grants, timeout
  accounting, and recent completions are atomically persisted at
  `%LOCALAPPDATA%\GitHubCopilot\HeavyJobQueue\queue-state.json`.
- After a broker restart, waiting wrappers reconnect with their stable request
  IDs. Active jobs continue in their caller processes, and their named leases
  keep automatic grants blocked until they report completion or exit.
- A disconnected request is removed only after its wrapper lease ends.

The broker never bypasses the named pipe. A new invocation exits with a clear
error when the broker is unavailable; a request already accepted by the broker
waits for it to restart and reclaims its durable entry.

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

For the one-time upgrade from protocol v1, wait until the old queue is completely
idle before exiting it. Protocol-v1 wrappers do not own recoverable request
leases. After protocol v2 is installed, accepted jobs survive ordinary broker
restarts and subsequent upgrades.

This publishes a framework-dependent application to
`$HOME\.copilot\tools\HeavyJobQueue\HeavyJobQueue.exe`, installs the replacement wrapper at
`$HOME\.copilot\tools\Invoke-HeavyJob.ps1`, installs modular instructions at
`$HOME\.copilot\instructions\heavy-job-queue.instructions.md`, and launches the
tray app. The installer does not edit or replace
`$HOME\.copilot\copilot-instructions.md`.

Running the explicit installer registers the tray app for per-user startup.
Pass `-DisableStartup` to remove or skip that registration:

```powershell
.\scripts\Install-HeavyJobQueue.ps1 -DisableStartup
```

Use `-NoLaunch` to install without starting the application. The installer uses
an isolated temporary publish directory. `-EnableStartup` remains accepted for
compatibility but is no longer required.
Use `-SkipInstructions` if you only want the application and wrapper. Existing
instructions at the managed path are updated only when they contain Heavy Job
Queue's ownership marker; an unrelated file is never overwritten.

Copilot CLI loads modular user instructions from
`$HOME\.copilot\instructions\**\*.instructions.md`. Restart active sessions
after installation, then use `/instructions` to confirm or disable the file.
See GitHub's
[custom instructions documentation](https://docs.github.com/en/copilot/how-tos/copilot-cli/customize-copilot/add-custom-instructions).

WPF does not support trimming or Native AOT in .NET 10. The lightweight
framework-dependent install reuses the installed .NET Desktop Runtime; publishing
WPF as a bundled single file would add roughly 170 MB of native rendering
libraries without reducing the app's steady-state runtime overhead.

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

While a job waits, the wrapper prints a heartbeat with the elapsed wait, the
last reported queue position, and the time left before it gives up. The interval
starts at `-HeartbeatSeconds` (default 60) and doubles up to 15 minutes, so a
long wait stays readable. A queued session is therefore visibly waiting instead
of silently producing no output, which is what a hang looks like to a Copilot
session:

```text
Heavy job queued at position 2: Run benchmarks
Heavy job still waiting after 00:01:00 at queue position 2; gives up in 03:59:00: Run benchmarks
```

`-PauseTimeoutMinutes` (default 60) bounds how long an operator pause can hold
this job. The wrapper cancels its queue entry and fails once that elapses, so a
forgotten **Pause all** cannot block a session indefinitely.

The tray menu opens the queue window, pauses or resumes the whole queue, or
exits the broker. The window shows all active jobs and waiters with label,
process ID, working directory, and elapsed time. Select a waiting row and use
**Move up** or **Move down**.

**Run now** is an explicit manual override for times when you judge that the
machine can handle concurrent work. The selected waiter is granted immediately
without a confirmation dialog, even when another job is active. You may approve
multiple overrides. Automatic FIFO grants remain blocked until every
active and overridden job finishes.

**Pause / resume** moves a selected waiter to or from a paused section at the
bottom of the queue. New jobs and unpaused waiters pass paused jobs. Resuming
appends the job behind current waiters but ahead of jobs that remain paused.
Time spent paused does not count against the wrapper's queue wait timeout, but
it does count against the wrapper's own `-PauseTimeoutMinutes` cap.

**Pause all / Resume all** holds the entire queue. Every current waiter and
every job that arrives afterwards is paused until you resume, and a banner
across the top of the window shows that the queue is held. Active jobs are
untouched and keep running to completion; when one finishes, no waiter is
granted. Held wrappers are told they are paused, so a queued session reports
why it is waiting instead of appearing to hang.

Two operator overrides still work while the queue is paused: **Run now** grants
the selected job immediately, and resuming a single waiter exempts just that job
so it can run under the normal FIFO rules. Pausing an exempted job returns it to
the queue-wide hold. Jobs you paused individually stay paused through
**Resume all**; the `Status` column distinguishes `Queue paused` from `Paused`.
As with individual pause, time spent globally paused does not count against the
wrapper's wait timeout, and the paused queue is restored after a broker restart.
Each wrapper still enforces its own `-PauseTimeoutMinutes` cap, so a queue left
paused fails its waiters with a clear reason rather than holding them forever.

Hover over an active, waiting, or paused row to see the complete PowerShell
scriptblock text submitted by the wrapper.

The queue window also includes Task Manager-style 60-second utilization history:
one graph per logical processor plus physical-memory usage. Sampling uses native
Windows system information APIs and does not require performance-counter
packages or an elevated process.

## Protocol and security

Protocol v2 uses the named pipe `GitHubCopilot.HeavyJobQueue.v2` with
`PipeOptions.CurrentUserOnly`. Each UTF-8 line is one JSON object. Clients send:

1. `enqueue` with a stable request ID, label, PID, current directory, enqueue
   timestamp, wait timeout, and per-request named lease.
2. `complete` after the caller-process scriptblock finishes, or `cancel` while
   waiting.

The broker responds with `queued`, `grant`, `ack`, or an explicit `error`.
Malformed messages, unsupported versions, invalid fields, and unexpected state
transitions are rejected. The queue-state snapshot is written through a
same-volume temporary file and atomically replaced; the previous valid snapshot
is retained as a backup.

## Troubleshooting

**Broker unavailable**: Start
`$HOME\.copilot\tools\HeavyJobQueue\HeavyJobQueue.exe` and retry.

**Upgrade cannot replace files**: Exit Heavy Job Queue from its tray menu, then
rerun the installer.

**Queue timeout**: Increase `-TimeoutMinutes`. Closing or interrupting the
wrapper releases its named lease, after which the broker removes its queue
entry. Time spent paused does not count against the timeout, including across a
broker restart.

**A session looks hung while queued**: It is usually waiting behind a
long-running job, which the tray window shows as the current active job. Read
the wrapper's heartbeat output to confirm it is still queued and see its
position; lower `-HeartbeatSeconds` for more frequent updates. Use **Run now**
to grant a waiter immediately when the machine can take the extra load.

## License

[MIT](LICENSE)

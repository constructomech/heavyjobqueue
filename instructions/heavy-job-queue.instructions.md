---
applyTo: "**"
---
<!-- managed-by: heavyjobqueue -->

# Shared heavy jobs

Heavy jobs include compilation, builds, CMake configuration, test execution, and
benchmarking. Builds, compilation, configuration, and tests use shared access and
may run concurrently across local Copilot sessions. Benchmarks require exclusive
access and must run alone.

Never run a compilation, CMake configure, test, or benchmark command directly.
Run each heavy command through the queue wrapper:

```powershell
& "$HOME\.copilot\tools\Invoke-HeavyJob.ps1" "<short job description>" { <command> }
```

The wrapper uses shared access by default. Add `-Exclusive` only for benchmarks:

```powershell
& "$HOME\.copilot\tools\Invoke-HeavyJob.ps1" "Run benchmarks" { <command> } -Exclusive
```

Because shared jobs may overlap, limit each build, compilation, or test command
to at most four internal workers. Use the tool's equivalent of `-j 4`,
`--parallel 4`, or `-m:4` when supported, and preserve any stricter existing
limit.

Acquire the queue slot only for the actual heavy command. Do not hold it while
planning, reasoning, editing, or reviewing output. If the tray broker or wrapper
is unavailable, report the problem and stop; do not bypass the queue.

An exclusive job may wait for active shared jobs, and shared jobs behind an
exclusive waiter may wait to preserve FIFO fairness. While queued, the wrapper
prints a heartbeat with its queue position and remaining wait, and it exits on
its own when the job finishes or the wait times out. Silence from a running
wrapper means it is still queued or running; it is not a lost completion
notification. Wait for it, and read the pending output to see the latest
heartbeat. Do not re-run the command, shorten the wait, or run the heavy command
directly, since a second submission only adds another queue entry.

If the wrapper reports that the queue operator paused the job, it keeps its place
and waits until the operator resumes it; it does not cancel itself. Keep waiting
and report that the queue is paused rather than resubmitting the job.

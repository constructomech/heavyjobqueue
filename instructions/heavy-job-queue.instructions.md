---
applyTo: "**"
---
<!-- managed-by: heavyjobqueue -->

# Shared heavy jobs

Compilation, CMake configuration, test execution, and benchmarking are shared
heavy jobs and must be serialized across all local Copilot sessions on this
machine.

Never run a compilation, CMake configure, test, or benchmark command directly.
Run each heavy command through the queue wrapper:

```powershell
& "$HOME\.copilot\tools\Invoke-HeavyJob.ps1" "<short job description>" { <command> }
```

Acquire the queue slot only for the actual heavy command. Do not hold it while
planning, reasoning, editing, or reviewing output. If the tray broker or wrapper
is unavailable, report the problem and stop; do not bypass the queue.

A heavy job may wait a long time behind another session's job. While queued, the
wrapper prints a heartbeat with its queue position and remaining wait, and it
exits on its own when the job finishes or the wait times out. Silence from a
running wrapper means it is still queued or the job is still running; it is not
a lost completion notification. Wait for it, and read the pending output to see
the latest heartbeat. Do not re-run the command, shorten the wait, or run the
heavy command directly, since a second submission only adds another queue entry.

If the wrapper reports that the queue operator paused the job, it keeps its place
and waits until the operator resumes it; it does not cancel itself. Keep waiting
and report that the queue is paused rather than resubmitting the job.

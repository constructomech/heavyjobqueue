---
applyTo: "**"
---
<!-- managed-by: heavyjobqueue -->

# Shared heavy jobs

Compilation, CMake configuration, and benchmarking are shared heavy jobs and
must be serialized across all local Copilot sessions on this machine.

Never run a compilation, CMake configure, or benchmark command directly. Run
each heavy command through the queue wrapper:

```powershell
& "$HOME\.copilot\tools\Invoke-HeavyJob.ps1" "<short job description>" { <command> }
```

Acquire the queue slot only for the actual heavy command. Do not hold it while
planning, reasoning, editing, or reviewing output. If the tray broker or wrapper
is unavailable, report the problem and stop; do not bypass the queue or legacy
lock.

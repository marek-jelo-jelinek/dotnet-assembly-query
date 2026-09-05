# Background daemon

Loading every DLL/PDB from scratch is the slow part of a `daq` call. To avoid paying that cost
on every invocation, `daq` transparently keeps a warm background daemon per distinct DLL set.

## How it kicks in

You don't opt into this - it just happens:

1. The first query against a given `--assembly`/`--dir` set runs entirely in-process, exactly
   as if there were no daemon (same output, no extra latency).
2. As a side effect, that same invocation spawns a detached daemon process for that DLL set
   (identified by a signature computed from the resolved DLL paths) and exits normally.
3. Every later query against the *same* DLL set is answered by that daemon over a local named
   pipe instead of reloading anything.

The daemon re-checks each file's size/timestamp before answering, so a real rebuild is always
picked up. That check is timestamp-based, not content-based - a `touch` or a cache restore
with no real content change can trigger one extra (harmless) reload.

It shuts itself down after 30 minutes of inactivity. Override with `--daemon-idle-timeout
<seconds>` on the query, or the `DAQ_DAEMON_IDLE_TIMEOUT_SECONDS` env var.

## Opting out

Pass `--no-daemon` to any query command to skip the mechanism entirely for that call: no
connect attempt, no spawn. Use this in CI or a sandboxed environment where a lingering
background process isn't wanted.

If a spawn attempt itself fails (e.g. the OS refuses to detach the process), that failure is
swallowed silently by default - the query result is unaffected either way. Set `DAQ_DEBUG=1`
to see the warning on stderr.

## `daemon status` / `stop` / `start`

```
daq daemon status [--json]
```
Lists live daemons (signature, pid, number of DLLs loaded, uptime). Stale entries (process no
longer alive) are pruned automatically.

```
daq daemon stop [--assembly <path>]... [--dir <path>]... [--json]
```
Stops the daemon matching that DLL set, or every running daemon if no `--assembly`/`--dir` is
given. Tries a graceful shutdown over the pipe first, falls back to killing the process.

```
daq daemon start --foreground --dir ./bin/Debug/net8.0 [--daemon-idle-timeout <seconds>] [--json]
```
Runs a daemon for the given DLL set in the foreground (blocks, logs to the current console) -
useful for debugging the daemon itself. Without `--foreground` it spawns detached, same as the
side-effect path above, and reports `started` or `already-running`.

## Where state lives

Each live daemon is registered as one JSON file under a per-user local-app-data directory
(`daq/daemons/<signature>.json` under `%LOCALAPPDATA%` on Windows, the platform equivalent
elsewhere). This registry is informational only - it's what `status`/`stop` read; a query
client never needs it; and it doesn't need to be found/cleaned up by hand.

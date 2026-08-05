# Codex Usage Notifier 5.0 for Windows

Codex Usage Notifier watches the rate limits attached to a ChatGPT-backed
Codex account. It shows a desktop alert when capacity becomes available again
and keeps a small live usage widget on the desktop.

Version 5.0 replaces the old plain system-style popup with a custom modern WPF
interface and adds an always-available usage bubble.

## What is included

### Modern event popup

The popup appears only for meaningful events, including:

- remaining capacity increases;
- a meter crosses from 1 percent or less to above 1 percent;
- a server-reported reached state clears; or
- an additional reset credit becomes available.

The popup includes:

- a concise event headline and explanation;
- the exact meter associated with a pending alert where available;
- a circular remaining-usage indicator;
- the current percentage and reset countdown;
- an **Open usage** action;
- a **Dismiss** action;
- keyboard support for Enter and Escape;
- hover-to-pause automatic dismissal;
- a soft fade and slide animation.

Windows toast, tray balloon, repeated sound, and taskbar flashing remain as
redundant channels. The custom popup is the primary visual channel.

### Compact live usage widget

The live widget is a 226 by 78 pixel rounded desktop pill. It:

- displays the current remaining percentage;
- updates its reset countdown every second;
- reads only confirmed monitor data;
- uses the pending alert snapshot while an alert is awaiting delivery;
- shows normal, low, empty, stale, waiting, and error states;
- stays above other windows by default without taking keyboard focus;
- remembers its desktop position;
- supports multiple Codex usage meters.

Widget controls:

- **Drag** the widget to move it.
- **Double-click** it to open the Codex Usage page.
- **Right-click** it, or select the gear button, to open its menu.
- Select a specific usage window or use **Auto: lowest remaining**.
- Toggle **Always on top**.
- Select **Reset widget position** to return it to the desktop edge.

## Reliability boundary

No local utility can guarantee notification delivery during every service,
network, operating-system, session, audio, or hardware failure. The Codex App
Server interface can also change. This package therefore validates each layer
it can validate and fails visibly rather than silently.

The installer does not report a validated installation until the target PC
passes live authentication, structured rate-limit access, popup, sound,
Scheduled Task, monitor heartbeat, UI heartbeat, and diagnostic checks. The
visual popup and widget checks require your confirmation.

## Detection method

The notifier does not scrape a browser page. It communicates with the locally
installed Codex CLI App Server and reads structured rate-limit data through:

```text
account/rateLimits/read
account/rateLimits/updated
```

It calculates remaining capacity as `100 - usedPercent`, listens for update
events, and polls every 60 seconds as a fallback.

A possible increase must survive a second structured reading before it is
accepted. Installation records the current values as a baseline, so setup does
not create a false reset alert.

## Reliability mechanisms

- Two-read freshness probe after startup, reconnection, and update events.
- Multiple matching readings before an increase notification is accepted.
- Rejection of quota windows whose reset timestamp regresses.
- Conservative matching when a service limit identifier changes.
- Atomic primary and redundant state files.
- Pending alerts saved before delivery and retried after restart.
- Alert deferral while the interactive Windows desktop is locked.
- Modern topmost popup plus toast, tray balloon, sound, and taskbar flashing.
- Separate interactive Scheduled Tasks for monitoring and the live widget.
- Independent watchdog for stale monitor and UI heartbeats.
- Rotating monitor, App Server, notification, UI, and watchdog logs.
- Transactional upgrades with restoration of the prior files, task set, and
  enabled or running task states when installation fails.

## Requirements

- Windows 10 or Windows 11
- Python 3.10 or newer
- Current Codex CLI
- ChatGPT-backed Codex authentication
- An interactive Windows session for visible alerts and the widget

The notifier does not ask for or save your ChatGPT password. Codex CLI manages
its own authentication cache and token refresh.

## Installation

1. Extract the ZIP.
2. Open the extracted `CodexUsageNotifier` folder.
3. Double-click `INSTALL.cmd`.
4. Confirm that you saw the modern popup, heard the test sound, and saw the
   compact live usage widget when prompted.

The command-file installer can install Codex when it is missing and update an
existing Codex CLI.

PowerShell installation is also supported:

```powershell
Set-ExecutionPolicy -Scope Process Bypass
.\install.ps1 -InstallCodexIfMissing -UpdateCodex
```

Use device-code authentication:

```powershell
.\install.ps1 -InstallCodexIfMissing -UpdateCodex -UseDeviceCode
```

Install and validate the package but leave all managed tasks disabled:

```powershell
.\install.ps1 -DoNotStart
```

## Test the interface later

Show the full realistic reset alert:

```powershell
.\test-reset-alert.ps1
```

Show the notification-channel test:

```powershell
.\test-alert.ps1
```

Reset the persistent widget position:

```powershell
.\reset-widget-position.ps1
```

## Show current status

```powershell
.\show-status.ps1
```

This reports live usage, all three Scheduled Tasks, monitor and UI heartbeat
ages, pending alerts, recent errors, and the last delivered event.

## Diagnostics and authentication repair

```powershell
.\run-diagnostics.ps1
.\repair-login.ps1
```

Device-code login:

```powershell
.\repair-login.ps1 -UseDeviceCode
```

## Start and stop

```powershell
.\stop-monitor.ps1
.\start-monitor.ps1
```

These scripts control the monitor, live widget, and watchdog as one managed
set.

## Configuration

The installed configuration is stored at:

```text
%LOCALAPPDATA%\CodexUsageNotifier\config.json
```

The application is installed at:

```text
%LOCALAPPDATA%\Programs\CodexUsageNotifier
```

Important configuration values include:

- `poll_seconds`: normal fallback polling interval;
- `post_reset_poll_seconds`: faster polling immediately after an expected
  reset;
- `minimum_increase_percent`: smallest confirmed increase that can alert;
- `notify_above_percent`: threshold crossing level, default 1 percent;
- `monitor_limit_ids`: optional list of service limit identifiers;
- `usage_url`: page opened by the popup and widget;
- `notification.popup_seconds`: popup lifetime;
- notification channel switches for toast, tray balloon, popup, and sound;
- `ui.live_widget`: enables or disables the persistent widget;
- `ui.always_on_top`: default topmost behavior;
- `ui.preferred_meter`: a meter key or `auto`;
- `ui.stale_after_seconds`: when displayed data becomes stale;
- `ui.refresh_milliseconds`: widget display refresh interval, from 500 to 5000;
- `ui.show_reset_countdown`: shows or hides the reset countdown.

The program rejects a configuration that disables every visible notification
channel.

## Data and logs

Runtime data is stored under:

```text
%LOCALAPPDATA%\CodexUsageNotifier
```

Relevant files:

```text
config.json
state.json
state.backup.json
heartbeat.json
ui-heartbeat.json
ui-state.json
monitor.log
app-server.log
notification.log
ui.log
watchdog.log
```

`ui-state.json` stores only the widget position, selected meter, and topmost
preference.

## Uninstall

Remove the Scheduled Tasks:

```powershell
.\uninstall.ps1
```

Also remove configuration, state, widget position, heartbeats, and logs:

```powershell
.\uninstall.ps1 -RemoveData
```

## Security notes

- The notifier launches only the configured local Codex CLI and its packaged
  Windows PowerShell helpers.
- It does not submit model prompts or run Codex model turns.
- It reads account type and rate-limit metadata only.
- It does not expose authentication tokens in the interface or logs.
- Do not share your Codex authentication cache or local profile data.

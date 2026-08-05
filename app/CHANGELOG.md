# Changelog

## 5.0.0

- Replaced the plain alert dialog with a custom modern WPF popup.
- Added a compact 226 by 78 pixel live usage widget with a circular progress
  indicator, remaining percentage, and reset countdown.
- Added normal, low, empty, waiting, stale, and error widget states.
- Added drag positioning, position persistence, meter selection, always-on-top
  control, usage-page access, configuration access, logs, and diagnostics.
- Added a dedicated interactive UI Scheduled Task and an independent UI
  heartbeat.
- Updated the watchdog to recover either the monitor or the live widget when
  its heartbeat becomes stale.
- Made the event popup prefer the exact meter stored with a pending alert.
- Added smooth popup and widget entrance animations, keyboard handling,
  hover-to-pause auto-dismiss, and modern controls.
- Added schema 5 UI configuration with validated refresh and stale-data
  settings.
- Added widget-aware status, start, stop, uninstall, diagnostics, and position
  reset tools.
- Added transactional upgrade rollback that restores the prior task
  definitions and their exact enabled and running states.
- Added installer gates for the modern popup, sound, live widget, monitor
  heartbeat, UI heartbeat, and final diagnostics.
- Expanded automated unit, protocol, UI-asset, configuration, release, and
  rollback coverage to 47 tests.

## 4.0.0

- Replaced browser scraping with structured Codex App Server rate-limit reads.
- Added `account/rateLimits/updated` event listening plus 60-second fallback
  polling.
- Added primary and secondary quota-window normalization and remaining-capacity
  calculation.
- Added freshness probing, multi-read increase confirmation, stale-window
  rejection, and conservative limit-ID matching.
- Added redundant atomic state persistence and durable pending-alert retries.
- Added alerting for confirmed increases, threshold crossings, reached-state
  clearing, and reset credits.
- Added topmost popup, toast, tray balloon, sound, taskbar flashing, lock-screen
  deferral, and heartbeat watchdog behavior.

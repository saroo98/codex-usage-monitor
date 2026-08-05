from __future__ import annotations

import json
import re
import unittest
from pathlib import Path
from xml.etree import ElementTree as ET

ROOT = Path(__file__).resolve().parents[1]


def read(name: str) -> str:
    return (ROOT / name).read_text(encoding="utf-8-sig")


def powershell_structure(text: str) -> None:
    """Conservative release check for quoting and delimiter structure."""
    if "\x00" in text:
        raise AssertionError("NUL byte in PowerShell source")

    incompatible = {
        r"(?<![&])&&(?![&])": "PowerShell 7 && operator",
        r"(?<![|])\|\|(?![|])": "PowerShell 7 || operator",
        r"\?\?": "PowerShell 7 null-coalescing operator",
        r"\?\.(?!\d)": "PowerShell 7 null-conditional operator",
        r"\bForEach-Object\s+-Parallel\b": "PowerShell 7 parallel pipeline",
    }
    for pattern, label in incompatible.items():
        if re.search(pattern, text):
            raise AssertionError(label)

    value = re.sub(r"<#.*?#>", "", text, flags=re.S)
    lines = value.splitlines()
    stripped_lines: list[str] = []
    terminator: str | None = None
    for line in lines:
        clean = line.strip()
        if terminator is not None:
            if clean == terminator:
                terminator = None
            stripped_lines.append("")
            continue
        if re.search(r'@"\s*$', line):
            terminator = '"@'
            stripped_lines.append(re.sub(r'@"\s*$', "", line))
            continue
        if re.search(r"@'\s*$", line):
            terminator = "'@"
            stripped_lines.append(re.sub(r"@'\s*$", "", line))
            continue
        stripped_lines.append(line)
    if terminator is not None:
        raise AssertionError("Unterminated here-string")

    value = "\n".join(stripped_lines)
    output: list[str] = []
    quote: str | None = None
    index = 0
    while index < len(value):
        char = value[index]
        if quote == "'":
            if char == "'" and index + 1 < len(value) and value[index + 1] == "'":
                index += 2
                continue
            if char == "'":
                quote = None
            index += 1
            continue
        if quote == '"':
            if char == "`":
                index += 2
                continue
            if char == '"':
                quote = None
            index += 1
            continue
        if char in ("'", '"'):
            quote = char
            index += 1
            continue
        if char == "#":
            while index < len(value) and value[index] != "\n":
                index += 1
            continue
        output.append(char)
        index += 1
    if quote is not None:
        raise AssertionError("Unterminated string")

    pairs = {"(": ")", "{": "}", "[": "]"}
    reverse = {right: left for left, right in pairs.items()}
    stack: list[str] = []
    for char in "".join(output):
        if char in pairs:
            stack.append(char)
        elif char in reverse:
            if not stack or stack[-1] != reverse[char]:
                raise AssertionError(f"Unbalanced delimiter {char}")
            stack.pop()
    if stack:
        raise AssertionError(f"Unclosed delimiter {stack[-1]}")


class UiAssetTests(unittest.TestCase):
    def test_xaml_documents_parse(self):
        for name in ("popup.xaml", "live-widget.xaml"):
            root = ET.parse(ROOT / name).getroot()
            self.assertTrue(root.tag.endswith("Window"))

    def test_popup_design_and_named_controls(self):
        value = read("popup.xaml")
        for marker in (
            'Width="520"',
            'Height="348"',
            'CornerRadius="22"',
            "DropShadowEffect",
            'x:Name="StateIcon"',
            'x:Name="HeadlineText"',
            'x:Name="BodyText"',
            'x:Name="StatusProgressArc"',
            'x:Name="StatusPercentText"',
            'x:Name="StatusResetText"',
            'x:Name="PrimaryButton"',
            'x:Name="DismissButton"',
        ):
            self.assertIn(marker, value)

    def test_popup_is_frameless_and_topmost(self):
        value = read("popup.xaml")
        self.assertIn('WindowStyle="None"', value)
        self.assertIn('AllowsTransparency="True"', value)
        self.assertIn('Topmost="True"', value)
        self.assertIn('ResizeMode="NoResize"', value)

    def test_widget_is_compact_and_non_activating(self):
        value = read("live-widget.xaml")
        for marker in (
            'Width="226"',
            'Height="78"',
            'ShowInTaskbar="False"',
            'ShowActivated="False"',
            'x:Name="ProgressArc"',
            'x:Name="PercentText"',
            'x:Name="UsageDetail"',
            'x:Name="MenuButton"',
        ):
            self.assertIn(marker, value)

    def test_notification_helper_uses_modern_wpf(self):
        value = read("notify.ps1")
        self.assertIn("popup.xaml", value)
        self.assertIn("System.Windows.Markup.XamlReader", value)
        self.assertIn("Bring-CodexWindowToAttention", value)
        self.assertIn("Write-Acknowledgement", value)
        self.assertNotIn("System.Windows.Forms.Form", value)

    def test_popup_prefers_pending_alert_event_meter(self):
        value = read("notify.ps1")
        self.assertIn('PSObject.Properties["pending_alert"]', value)
        self.assertIn('PSObject.Properties["events"]', value)
        self.assertIn('PSObject.Properties["key"]', value)
        self.assertIn("Select-CodexMeter", value)

    def test_live_widget_implements_expected_interactions(self):
        value = read("live-widget.ps1")
        for marker in (
            "Refresh display now",
            "Always on top",
            "Reset widget position",
            "Open Codex usage",
            "Double-click to open Codex Usage",
            "DragMove",
            "ContextMenu",
            "ui-heartbeat.json",
        ):
            self.assertIn(marker, value)

    def test_live_widget_reads_confirmed_and_pending_snapshots(self):
        common = read("ui-common.ps1")
        self.assertIn("Get-CodexLiveReading", common)
        self.assertIn('source = "pending-alert"', common)
        self.assertIn('source = "heartbeat"', common)
        self.assertIn('source = "state"', common)
        self.assertIn("priority = 3", common)

    def test_version_consistency(self):
        self.assertEqual(read("VERSION").strip(), "5.0.0")
        self.assertIn('VERSION = "5.0.0"', read("codex_usage_notifier.py"))
        self.assertIn("Codex Usage Notifier 5.0", read("README.md"))
        self.assertIn("## 5.0.0", read("CHANGELOG.md"))
        self.assertIn("Codex Usage Notifier 5.0 Installer", read("INSTALL.cmd"))

    def test_scheduler_registers_all_managed_tasks(self):
        value = read("install-task.ps1")
        for marker in (
            '"Codex Usage Notifier"',
            '"Codex Usage Notifier UI"',
            '"Codex Usage Notifier Watchdog"',
            "live-widget.ps1",
            "-Sta",
            "pythonw.exe",
            "LogonType Interactive",
        ):
            self.assertIn(marker, value)

    def test_management_scripts_include_ui_task(self):
        for name in (
            "start-monitor.ps1",
            "stop-monitor.ps1",
            "uninstall.ps1",
            "show-status.ps1",
            "watchdog.ps1",
        ):
            self.assertIn("Codex Usage Notifier UI", read(name), name)

    def test_installer_has_exact_task_rollback(self):
        value = read("install.ps1")
        for marker in (
            "Unregister-ScheduledTask -TaskName $name",
            "& $restoredTaskInstaller -InstallDir $InstallDir -DoNotStart",
            "$PriorTaskStates.ContainsKey($name)",
            "The prior scheduled task '$name' was not recreated.",
            "WasDisabled",
            "WasRunning",
        ):
            self.assertIn(marker, value)

    def test_installer_respects_disabled_widget_configuration(self):
        value = read("install.ps1")
        self.assertIn("$LiveWidgetEnabled = $true", value)
        self.assertIn("The live widget is disabled in config.json", value)
        self.assertIn("Wait-ForUiHeartbeat", value)
        self.assertIn("Do you see the small live Codex usage bubble", value)

    def test_config_example_contains_valid_ui_defaults(self):
        value = json.loads(read("config.example.json"))
        self.assertEqual(value["schema_version"], 5)
        self.assertTrue(value["ui"]["live_widget"])
        self.assertTrue(value["ui"]["always_on_top"])
        self.assertEqual(value["ui"]["preferred_meter"], "auto")
        self.assertGreaterEqual(value["ui"]["refresh_milliseconds"], 500)
        self.assertLessEqual(value["ui"]["refresh_milliseconds"], 5000)
        self.assertTrue(value["usage_url"].startswith("https://"))

    def test_powershell_sources_are_structurally_valid(self):
        scripts = sorted(ROOT.glob("*.ps1"))
        self.assertGreaterEqual(len(scripts), 16)
        for script in scripts:
            with self.subTest(script=script.name):
                powershell_structure(script.read_text(encoding="utf-8-sig"))

    def test_release_text_has_no_em_dash(self):
        paths = [
            *ROOT.glob("*.ps1"),
            *ROOT.glob("*.xaml"),
            ROOT / "README.md",
            ROOT / "CHANGELOG.md",
            ROOT / "codex_usage_notifier.py",
        ]
        for path in paths:
            with self.subTest(path=path.name):
                self.assertNotIn("\u2014", path.read_text(encoding="utf-8-sig"))


if __name__ == "__main__":
    unittest.main()

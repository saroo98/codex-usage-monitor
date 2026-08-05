using System.Net;
using CodexUsageMonitor.Core.Notifications;
using CodexUsageMonitor.Email.Models;

namespace CodexUsageMonitor.Email.Templates;

public static class UsageEmailTemplate
{
    public static EmailMessage Create(
        UsageTransition transition,
        string from,
        IReadOnlyList<string> to,
        bool includeAccountLabel,
        string? accountLabel)
    {
        ArgumentNullException.ThrowIfNull(transition);
        ArgumentException.ThrowIfNullOrWhiteSpace(from);
        ArgumentNullException.ThrowIfNull(to);
        if (to.Count is <= 0 or > 16 || to.Any(static address => string.IsNullOrWhiteSpace(address)))
        {
            throw new ArgumentException("Email templates require between one and sixteen recipients.", nameof(to));
        }
        var limit = transition.Current?.Label ?? "Selected Codex limit";
        var remaining = transition.Current?.RemainingPercent;
        var eventText = transition.Identity.EventType switch
        {
            NotificationEventType.Depleted => "is depleted",
            NotificationEventType.Reset => "has reset",
            NotificationEventType.ConnectionLost => "connection was interrupted",
            NotificationEventType.ConnectionRestored => "connection was restored",
            NotificationEventType.ResetCreditAvailable => "has a reset credit available",
            _ => remaining is null ? "changed" : $"has {remaining:0}% remaining",
        };
        var subject = $"Codex usage: {limit} {eventText}";
        var accountLine = includeAccountLabel && !string.IsNullOrWhiteSpace(accountLabel)
            ? $"Account: {accountLabel.Trim()}\n"
            : string.Empty;
        var resetLine = transition.Current?.ResetsAtUtc is { } reset
            ? $"Resets: {reset:yyyy-MM-dd HH:mm 'UTC'}\n"
            : string.Empty;
        var plain = $"{limit} {eventText}.\n{accountLine}{resetLine}Observed: {transition.OccurredAtUtc:yyyy-MM-dd HH:mm 'UTC'}\n\nOpen Codex Usage Monitor for current details.";
        var html = $"""
            <!doctype html>
            <html lang="en">
            <head><meta charset="utf-8"><meta name="viewport" content="width=device-width"></head>
            <body style="margin:0;background:#0f1115;color:#f6f7fb;font-family:Segoe UI,Arial,sans-serif">
              <table role="presentation" width="100%" cellspacing="0" cellpadding="0" style="padding:28px 16px">
                <tr><td align="center">
                  <table role="presentation" width="560" cellspacing="0" cellpadding="0" style="max-width:560px;background:#171a21;border:1px solid #2b303b;border-radius:18px;padding:28px">
                    <tr><td style="font-size:12px;letter-spacing:.11em;text-transform:uppercase;color:#9aa3b2">Codex Usage Monitor</td></tr>
                    <tr><td style="padding-top:14px;font-size:24px;font-weight:700">{WebUtility.HtmlEncode(limit)}</td></tr>
                    <tr><td style="padding-top:8px;font-size:17px;color:#d8dde7">{WebUtility.HtmlEncode(eventText)}.</td></tr>
                    {(includeAccountLabel && !string.IsNullOrWhiteSpace(accountLabel) ? $"<tr><td style=\"padding-top:18px;color:#9aa3b2\">Account: {WebUtility.HtmlEncode(accountLabel.Trim())}</td></tr>" : string.Empty)}
                    {(transition.Current?.ResetsAtUtc is { } resetAt ? $"<tr><td style=\"padding-top:6px;color:#9aa3b2\">Resets {resetAt:yyyy-MM-dd HH:mm} UTC</td></tr>" : string.Empty)}
                    <tr><td style="padding-top:24px;font-size:12px;color:#737d8d">Observed {transition.OccurredAtUtc:yyyy-MM-dd HH:mm} UTC</td></tr>
                  </table>
                </td></tr>
              </table>
            </body>
            </html>
            """;
        return new EmailMessage(from.Trim(), to.Select(static address => address.Trim()).ToArray(), subject, plain, html, transition.Identity.Value);
    }
}

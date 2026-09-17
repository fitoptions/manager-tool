using System.Windows.Automation;

namespace ManagerTool.Agent.Capture;

/// <summary>
/// Best-effort extraction of the current browser address-bar value using UI Automation.
/// This reads the URL already displayed in the browser chrome — it does not intercept
/// network traffic or decrypt anything. May return null (address bar not focusable,
/// browser not exposing the pattern, PWA windows, etc.), which is fine.
/// </summary>
public static class BrowserUrlReader
{
    // NOTE: UIAutomation lives in UIAutomationClient/UIAutomationTypes assemblies, which
    // are part of the Windows Desktop (WPF) framework reference — available because this
    // project targets net8.0-windows with UseWPF.
    public static string? TryReadUrl(IntPtr windowHandle)
    {
        try
        {
            var root = AutomationElement.FromHandle(windowHandle);
            if (root is null)
                return null;

            // The address bar is an Edit control that supports ValuePattern.
            var condition = new AndCondition(
                new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Edit),
                new PropertyCondition(AutomationElement.IsValuePatternAvailableProperty, true));

            var edit = root.FindFirst(TreeScope.Descendants, condition);
            if (edit is null)
                return null;

            if (edit.TryGetCurrentPattern(ValuePattern.Pattern, out var patternObj)
                && patternObj is ValuePattern value)
            {
                var url = value.Current.Value;
                return string.IsNullOrWhiteSpace(url) ? null : Normalize(url);
            }
        }
        catch
        {
            // UIA can throw for protected/elevated windows or during teardown; ignore.
        }
        return null;
    }

    private static string Normalize(string raw)
    {
        raw = raw.Trim();
        // Address bars often omit the scheme; keep it readable for the reviewer.
        if (!raw.Contains("://") && !raw.StartsWith("about:", StringComparison.OrdinalIgnoreCase))
            raw = "http://" + raw;
        return raw;
    }
}

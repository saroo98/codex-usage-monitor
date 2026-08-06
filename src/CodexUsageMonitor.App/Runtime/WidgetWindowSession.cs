using System.Windows;

namespace CodexUsageMonitor.App.Runtime;

public interface IWidgetWindow
{
    event EventHandler? Closed;

    bool IsVisible { get; }
    Window OwnerWindow { get; }

    void ShowWithoutActivation();
    void Hide();
    void RestorePlacement();
    void CloseForExit();
}

public sealed class WidgetWindowSession : IDisposable
{
    private readonly Func<IWidgetWindow> _factory;
    private IWidgetWindow? _window;
    private bool _disposed;

    public WidgetWindowSession(Func<IWidgetWindow> factory) =>
        _factory = factory ?? throw new ArgumentNullException(nameof(factory));

    public bool IsVisible => _window?.IsVisible is true;
    public Window? VisibleOwner => IsVisible ? _window?.OwnerWindow : null;

    public void Show()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var window = GetOrCreateWindow();
        window.ShowWithoutActivation();
    }

    public void Hide()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _window?.Hide();
    }

    public void RestorePlacement()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _window?.RestorePlacement();
    }

    public void CloseForExit()
    {
        if (_disposed || _window is not { } window)
        {
            return;
        }

        window.CloseForExit();
    }

    private IWidgetWindow GetOrCreateWindow()
    {
        if (_window is not null)
        {
            return _window;
        }

        var window = _factory() ?? throw new InvalidOperationException("The widget window factory returned no window.");
        window.Closed += OnWindowClosed;
        _window = window;
        return window;
    }

    private void OnWindowClosed(object? sender, EventArgs eventArgs)
    {
        if (_window is not { } window || !ReferenceEquals(sender, window))
        {
            return;
        }

        window.Closed -= OnWindowClosed;
        _window = null;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        CloseForExit();
        _disposed = true;
    }
}

using System.Drawing;
using System.IO;
using System.Windows;

namespace MediaCatalog.App.Infrastructure;

/// <summary>
/// The notification-area icon: balloon notifications for newly found files, and a menu to
/// bring the window back or quit. Also what keeps the app usable when it starts hidden at
/// sign-in. Must be constructed on the UI thread.
/// </summary>
public sealed class TrayIcon : IDisposable
{
    private readonly System.Windows.Forms.NotifyIcon _icon;

    public TrayIcon(Action onOpen, Action onExit)
    {
        var menu = new System.Windows.Forms.ContextMenuStrip();
        menu.Items.Add("Open Media Catalog", null, (_, _) => onOpen());
        menu.Items.Add(new System.Windows.Forms.ToolStripSeparator());
        menu.Items.Add("Exit", null, (_, _) => onExit());

        _icon = new System.Windows.Forms.NotifyIcon
        {
            Icon = LoadIcon(),
            Visible = true,
            Text = "Media Catalog",
            ContextMenuStrip = menu
        };
        _icon.DoubleClick += (_, _) => onOpen();
    }

    public void Notify(string title, string message)
    {
        try
        {
            _icon.BalloonTipTitle = title;
            _icon.BalloonTipText = message;
            _icon.ShowBalloonTip(5000);
        }
        catch { /* notifications are best-effort */ }
    }

    /// <summary>The application icon, falling back to the system one if it can't be read.</summary>
    private static Icon LoadIcon()
    {
        try
        {
            var uri = new Uri("pack://application:,,,/Assets/MediaCatalog.ico");
            using var stream = Application.GetResourceStream(uri)?.Stream;
            // Pick the size Windows wants for the tray at the current DPI.
            if (stream != null)
                return new Icon(stream, System.Windows.Forms.SystemInformation.SmallIconSize);
        }
        catch { }
        return SystemIcons.Application;
    }

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.Dispose();
    }
}

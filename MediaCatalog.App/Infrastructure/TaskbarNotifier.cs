using System.Drawing;

namespace MediaCatalog.App.Infrastructure;

/// <summary>
/// Shows Windows taskbar notifications via a tray <see cref="System.Windows.Forms.NotifyIcon"/>.
/// Created lazily on first use; must be constructed on the UI thread.
/// </summary>
public sealed class TaskbarNotifier : IDisposable
{
    private readonly System.Windows.Forms.NotifyIcon _icon;

    public TaskbarNotifier()
    {
        _icon = new System.Windows.Forms.NotifyIcon
        {
            Icon = SystemIcons.Application,
            Visible = true,
            Text = "Media Catalog"
        };
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

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.Dispose();
    }
}

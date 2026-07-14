using System;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace SoundShell.App;

internal sealed class TrayIcon : IDisposable
{
    private readonly NotifyIcon notifyIcon;
    private readonly Icon icon;

    public TrayIcon(Action show, Action exit)
    {
        icon = LoadIcon();
        var menu = new ContextMenuStrip();
        menu.Items.Add("Show SoundShell", null, (_, _) => show());
        menu.Items.Add("Exit", null, (_, _) => exit());
        notifyIcon = new NotifyIcon
        {
            Text = "SoundShell",
            Icon = icon,
            ContextMenuStrip = menu,
            Visible = true
        };
        notifyIcon.DoubleClick += (_, _) => show();
    }

    private static Icon LoadIcon()
    {
        try
        {
            using var bitmap = new Bitmap(Path.Combine(AppContext.BaseDirectory, "Assets", "Square44x44Logo.png"));
            var handle = bitmap.GetHicon();
            try { return (Icon)Icon.FromHandle(handle).Clone(); }
            finally { DestroyIcon(handle); }
        }
        catch
        {
            return (Icon)SystemIcons.Application.Clone();
        }
    }

    public void Dispose()
    {
        notifyIcon.Visible = false;
        notifyIcon.ContextMenuStrip?.Dispose();
        notifyIcon.Dispose();
        icon.Dispose();
    }

    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr handle);
}

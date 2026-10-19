using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Windows.Forms;
using DriveSync.Core.Models;

namespace DriveSync.App.Services;

public class TrayService : IDisposable
{
    private readonly NotifyIcon _notifyIcon;
    private Action? _onOpenApp;
    private Action? _onToggleFlyout;
    private Action? _onReviewConflicts;
    private Action? _onPauseResumeAll;
    private Action? _onSyncAllNow;
    private Action? _onExit;

    public TrayService()
    {
        _notifyIcon = new NotifyIcon
        {
            Text = "DriveSync - Two-Way Synchronizer",
            Visible = true,
            Icon = CreateStatusIcon(SyncStatus.Idle)
        };

        _notifyIcon.Click += (s, e) =>
        {
            if (e is MouseEventArgs me && me.Button == MouseButtons.Left)
            {
                _onToggleFlyout?.Invoke();
            }
        };

        _notifyIcon.DoubleClick += (s, e) =>
        {
            if (e is MouseEventArgs me && me.Button == MouseButtons.Left)
            {
                _onOpenApp?.Invoke();
            }
        };

        RebuildContextMenu(isPaused: false);
    }

    public void Initialize(
        Action onOpenApp,
        Action onToggleFlyout,
        Action onReviewConflicts,
        Action onPauseResumeAll,
        Action onSyncAllNow,
        Action onExit)
    {
        _onOpenApp = onOpenApp;
        _onToggleFlyout = onToggleFlyout;
        _onReviewConflicts = onReviewConflicts;
        _onPauseResumeAll = onPauseResumeAll;
        _onSyncAllNow = onSyncAllNow;
        _onExit = onExit;

        RebuildContextMenu(isPaused: false);
    }

    public void UpdateStatus(SyncStatus status, string tooltip)
    {
        try
        {
            _notifyIcon.Icon?.Dispose();
            _notifyIcon.Icon = CreateStatusIcon(status);

            string safeText = $"DriveSync: {tooltip}";
            if (safeText.Length > 63) safeText = safeText.Substring(0, 60) + "...";
            _notifyIcon.Text = safeText;

            RebuildContextMenu(status == SyncStatus.Paused);
        }
        catch { }
    }

    public void ShowNotification(string title, string message, ToolTipIcon icon = ToolTipIcon.Info)
    {
        try
        {
            _notifyIcon.ShowBalloonTip(3000, title, message, icon);
        }
        catch { }
    }

    private void RebuildContextMenu(bool isPaused)
    {
        var menu = new ContextMenuStrip();
        var openItem = new ToolStripMenuItem("Open DriveSync", null, (s, e) => _onOpenApp?.Invoke());
        openItem.Font = new Font(openItem.Font, FontStyle.Bold);
        menu.Items.Add(openItem);

        menu.Items.Add(new ToolStripMenuItem("Sync All Now", null, (s, e) => _onSyncAllNow?.Invoke()));
        menu.Items.Add(new ToolStripMenuItem(isPaused ? "Resume All" : "Pause All", null, (s, e) => _onPauseResumeAll?.Invoke()));
        menu.Items.Add(new ToolStripMenuItem("Review Conflicts", null, (s, e) => _onReviewConflicts?.Invoke()));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(new ToolStripMenuItem("Exit DriveSync", null, (s, e) => _onExit?.Invoke()));

        _notifyIcon.ContextMenuStrip = menu;
    }

    private static Icon CreateStatusIcon(SyncStatus status)
    {
        int size = 32;
        using var bitmap = new Bitmap(size, size);
        using var g = Graphics.FromImage(bitmap);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.InterpolationMode = InterpolationMode.HighQualityBicubic;

        string pngPath = Path.Combine(AppContext.BaseDirectory, "Assets", "app.png");
        bool drewBase = false;

        if (File.Exists(pngPath))
        {
            try
            {
                using var baseImg = Image.FromFile(pngPath);
                g.DrawImage(baseImg, 0, 0, size, size);
                drewBase = true;
            }
            catch { }
        }

        Color baseColor = status switch
        {
            SyncStatus.UpToDate => Color.FromArgb(16, 185, 129),       // Emerald Green
            SyncStatus.Syncing or SyncStatus.Scanning => Color.FromArgb(14, 165, 233), // Vivid Sky Blue
            SyncStatus.Paused => Color.FromArgb(245, 158, 11),         // Amber
            SyncStatus.ConflictsDetected => Color.FromArgb(249, 115, 22), // Orange
            SyncStatus.Error or SyncStatus.AttentionRequired => Color.FromArgb(239, 68, 68), // Crimson Red
            _ => Color.FromArgb(100, 116, 139)                         // Slate Gray
        };

        if (!drewBase)
        {
            // Draw modern rounded shield/circle base
            using var brush = new SolidBrush(Color.FromArgb(30, 41, 59)); // Dark background
            g.FillEllipse(brush, 1, 1, size - 2, size - 2);

            using var ringPen = new Pen(baseColor, 3.5f);
            g.DrawEllipse(ringPen, 3, 3, size - 6, size - 6);

            using var centerBrush = new SolidBrush(baseColor);
            g.FillEllipse(centerBrush, 10, 10, size - 20, size - 20);
        }
        else
        {
            // Draw status badge pip in bottom-right corner
            int badgeSize = 10;
            int badgeX = size - badgeSize - 1;
            int badgeY = size - badgeSize - 1;

            using var borderBrush = new SolidBrush(Color.FromArgb(15, 23, 42));
            g.FillEllipse(borderBrush, badgeX - 1, badgeY - 1, badgeSize + 2, badgeSize + 2);

            using var pipBrush = new SolidBrush(baseColor);
            g.FillEllipse(pipBrush, badgeX, badgeY, badgeSize, badgeSize);
        }

        IntPtr hIcon = bitmap.GetHicon();
        return (Icon)Icon.FromHandle(hIcon).Clone();
    }

    public void Dispose()
    {
        _notifyIcon.Visible = false;
        _notifyIcon.Icon?.Dispose();
        _notifyIcon.Dispose();
    }
}

// ScreenSaverForm.cs
// Hockey Fight
//
// Hosts a HockeyFightRenderer. On macOS the ScreenSaver framework creates one
// ScreenSaverView per display and drives it with -animateOneFrame; here we create
// one of these forms per monitor and drive it from a timer.

using System.Diagnostics;
using System.Drawing;

namespace HockeyFight;

internal sealed class ScreenSaverForm : Form
{
    /// <summary>Matches [self setAnimationTimeInterval:1/30.0].</summary>
    private const int FrameIntervalMilliseconds = 33;

    /// <summary>Windows delivers spurious small mouse moves; ignore anything under this.</summary>
    private const int MouseMoveThreshold = 8;

    /// <summary>
    /// Input is ignored briefly after launch. Showing a full-screen window generates
    /// mouse and focus traffic of its own, and quitting on that would make the
    /// screensaver look like it never started.
    /// </summary>
    private const int InputGraceMilliseconds = 1000;

    private readonly HockeyFightRenderer _renderer = new();
    private readonly System.Windows.Forms.Timer _timer = new();
    private readonly bool _isPreview;
    private readonly IntPtr _previewHandle;

    private Point _lastMousePosition = Point.Empty;
    private bool _haveMousePosition;
    private bool _exiting;

    /// <summary>
    /// Exact window rectangle in physical pixels for the borderless modes. WinForms
    /// rescales a per-monitor-aware form when it lands on a monitor whose DPI differs
    /// from the process default, which would size a full-screen window past the edge
    /// of its own screen, so the rectangle is pinned and reapplied.
    /// </summary>
    private Rectangle? _targetBounds;

    private readonly Stopwatch _sinceShown = new();

    private bool InputArmed => _sinceShown.ElapsedMilliseconds > InputGraceMilliseconds;

    // The scene is laid out around a 1292x120 scoreboard, and the macOS original
    // runs on a Retina display where that fills roughly three quarters of the
    // width. To get the same proportions on a Windows desktop the whole scene is
    // drawn through an integer scale factor, chosen as the largest one that still
    // leaves room for the scoreboard plus a net tile either side, and for the band
    // the zamboni drives through.
    private const float MinLogicalWidth = 1292 + 2 * 80;
    private const float MinLogicalHeight = 560;

    /// <summary>
    /// Nominal full-screen scene used to shrink the whole rink into the settings
    /// dialog's thumbnail. Without this the preview would show a corner of the
    /// scene at full size, and the scoreboard would not fit at all.
    /// </summary>
    private const float PreviewDesignWidth = 1920;
    private const float PreviewDesignHeight = 1080;

    /// <summary>
    /// Zoom applied to the whole scene. 1 means the drawing is pixel-for-pixel
    /// identical to the non-Retina macOS original. Full screen always uses a whole
    /// number so the pixel art stays crisp; the preview thumbnail is free to use a
    /// fraction because it is a miniature.
    /// </summary>
    private float _scale = 1f;

    private ScreenSaverForm(bool isPreview, IntPtr previewHandle)
    {
        _isPreview = isPreview;
        _previewHandle = previewHandle;

        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                 ControlStyles.OptimizedDoubleBuffer, true);

        // Sizes here are exact pixel counts; keep WinForms from rescaling them.
        AutoScaleMode = AutoScaleMode.None;
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        BackColor = Color.FromArgb(0xf8, 0xf8, 0xf8);
        DoubleBuffered = true;
        KeyPreview = true;
        Text = "Hockey Fight";

        _timer.Interval = FrameIntervalMilliseconds;
        _timer.Tick += OnTick;
    }

    /// <summary>Full-screen instance covering a single monitor.</summary>
    public static ScreenSaverForm CreateFullScreen(Screen screen)
    {
        var form = new ScreenSaverForm(isPreview: false, previewHandle: IntPtr.Zero)
        {
            StartPosition = FormStartPosition.Manual,
            Bounds = screen.Bounds,
            TopMost = true,
        };
        form._targetBounds = screen.Bounds;
        return form;
    }

    /// <summary>
    /// Preview instance, reparented into the small monitor thumbnail that the
    /// Screen Saver settings dialog hands us via /p.
    /// </summary>
    public static ScreenSaverForm CreatePreview(IntPtr parentHandle)
    {
        var form = new ScreenSaverForm(isPreview: true, previewHandle: parentHandle)
        {
            StartPosition = FormStartPosition.Manual,
        };

        if (NativeMethods.GetClientRect(parentHandle, out NativeMethods.RECT parentRect))
        {
            form.Size = new Size(parentRect.Width, parentRect.Height);
            form._targetBounds = new Rectangle(0, 0, parentRect.Width, parentRect.Height);
        }

        return form;
    }

    /// <summary>Windowed instance, for development. Not used by Windows itself.</summary>
    public static ScreenSaverForm CreateWindowed(Size size)
    {
        var form = new ScreenSaverForm(isPreview: true, previewHandle: IntPtr.Zero)
        {
            StartPosition = FormStartPosition.CenterScreen,
            FormBorderStyle = FormBorderStyle.Sizable,
            ShowInTaskbar = true,
            ClientSize = size,
        };
        return form;
    }

    /// <summary>
    /// Makes this window a child of the settings dialog's preview area.
    /// </summary>
    /// <remarks>
    /// Done once the form is shown rather than as soon as its handle exists.
    /// WinForms recreates a form's window handle while it settles (changing styles
    /// destroys and rebuilds it), and a recreated handle comes back as a top-level
    /// window, silently undoing the reparenting.
    /// </remarks>
    private void AttachToPreviewParent()
    {
        // Reparent first: WS_CHILD on a window that still has no parent is invalid,
        // and setting it early makes SetParent fail outright.
        NativeMethods.SetParent(Handle, _previewHandle);

        // WS_POPUP then has to go, because a window cannot be both a popup and a
        // child; a borderless Form carries WS_POPUP, and leaving it set leaves the
        // preview blank.
        long style = NativeMethods.GetWindowLongPtr(Handle, NativeMethods.GWL_STYLE).ToInt64();
        style &= ~NativeMethods.WS_POPUP;
        style |= NativeMethods.WS_CHILD | NativeMethods.WS_VISIBLE;
        NativeMethods.SetWindowLongPtr(Handle, NativeMethods.GWL_STYLE, (IntPtr)style);
    }

    protected override void OnLoad(EventArgs e)
    {
        base.OnLoad(e);

        if (!_isPreview)
        {
            Cursor.Hide();
        }

        UpdateScale();
        _renderer.Start();
        _timer.Start();
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);

        if (_isPreview && _previewHandle != IntPtr.Zero)
        {
            AttachToPreviewParent();
        }

        // Showing the window can move it onto a monitor with a different DPI, which
        // is where WinForms would resize it out from under us.
        ApplyTargetBounds();

        _sinceShown.Restart();
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == NativeMethods.WM_DPICHANGED && _targetBounds is not null)
        {
            // Swallow the message so WinForms does not rescale a window whose size
            // is already expressed in physical pixels.
            ApplyTargetBounds();
            m.Result = IntPtr.Zero;
            return;
        }

        base.WndProc(ref m);
    }

    private void ApplyTargetBounds()
    {
        if (_targetBounds is not Rectangle target || !IsHandleCreated)
        {
            return;
        }

        NativeMethods.SetWindowPos(Handle, IntPtr.Zero,
            target.X, target.Y, target.Width, target.Height,
            NativeMethods.SWP_NOZORDER | NativeMethods.SWP_NOACTIVATE);

        UpdateScale();
        Invalidate();
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        UpdateScale();
        Invalidate();
    }

    /// <summary>
    /// Recomputes the scale factor and hands the renderer its logical size, which
    /// is the client size divided by that factor.
    /// </summary>
    private void UpdateScale()
    {
        Size client = PhysicalClientSize;
        if (client.Width <= 0 || client.Height <= 0)
        {
            return;
        }

        if (_isPreview)
        {
            // Fit the whole rink into the thumbnail rather than showing a corner of
            // it at full size.
            _scale = Math.Max(
                Math.Min(client.Width / PreviewDesignWidth, client.Height / PreviewDesignHeight),
                0.05f);
        }
        else
        {
            float fit = Math.Min(client.Width / MinLogicalWidth, client.Height / MinLogicalHeight);
            _scale = Math.Max(1, (int)MathF.Floor(fit));
        }

        _renderer.SetSize(client.Width / _scale, client.Height / _scale);
    }

    /// <summary>
    /// The client area in real pixels. Form.ClientSize reports DPI-scaled logical
    /// units, which on a 150% display is 1.5x the pixels actually being painted, so
    /// it cannot be used to lay out a scene measured in pixels.
    /// </summary>
    private Size PhysicalClientSize
    {
        get
        {
            if (IsHandleCreated && NativeMethods.GetClientRect(Handle, out NativeMethods.RECT rect))
            {
                return new Size(rect.Width, rect.Height);
            }

            return ClientSize;
        }
    }

    private void OnTick(object? sender, EventArgs e)
    {
        // The settings dialog destroys the preview window without telling us; when
        // that happens there is nothing left to draw into, so exit.
        if (_isPreview && _previewHandle != IntPtr.Zero && !NativeMethods.IsWindow(_previewHandle))
        {
            ExitScreenSaver();
            return;
        }

        _renderer.AnimateOneFrame();
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        Graphics g = e.Graphics;

        if (_scale != 1f)
        {
            g.ScaleTransform(_scale, _scale);
        }

        _renderer.Draw(g);
    }

    protected override void OnPaintBackground(PaintEventArgs e)
    {
        // The renderer paints every pixel; skipping the background fill avoids a
        // full-screen flash between frames.
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);

        if (_isPreview || !InputArmed)
        {
            return;
        }

        // Screen coordinates, not e.Location: the window is repositioned while it
        // settles onto its monitor, and client-relative coordinates jump when that
        // happens even though the mouse has not moved.
        Point position = Cursor.Position;

        if (!_haveMousePosition)
        {
            _lastMousePosition = position;
            _haveMousePosition = true;
            return;
        }

        if (Math.Abs(position.X - _lastMousePosition.X) > MouseMoveThreshold ||
            Math.Abs(position.Y - _lastMousePosition.Y) > MouseMoveThreshold)
        {
            ExitScreenSaver();
        }
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (!_isPreview && InputArmed)
        {
            ExitScreenSaver();
        }
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (!_isPreview && InputArmed)
        {
            ExitScreenSaver();
        }
    }

    private void ExitScreenSaver()
    {
        if (_exiting)
        {
            return;
        }

        _exiting = true;
        _timer.Stop();
        Application.Exit();
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        _timer.Stop();

        if (!_isPreview)
        {
            Cursor.Show();
        }

        base.OnFormClosing(e);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _timer.Dispose();
            _renderer.Dispose();
        }

        base.Dispose(disposing);
    }
}

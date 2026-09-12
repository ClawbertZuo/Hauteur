using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Reflection;
using System.Runtime.InteropServices;
using Hauteur.Interop;

namespace Hauteur.App;

/// <summary>
/// 启动加载界面:图标(Assets\NewIcon.png)以分层窗口居中浮现在主屏,
/// 鼠标穿透、不抢焦点、不占任务栏;短暂显示后平滑淡出。创建失败不影响正常启动。
/// </summary>
internal sealed class SplashWindow : IDisposable
{
    internal const string WindowTitle = "Hauteur.Splash";

    private const int FadeStepMs = 30;
    private const uint SWP_NOACTIVATE = 0x0010;

    private readonly IntPtr _hwnd;
    private readonly GlowInterop.WndProc _wndProcDelegate; // 常驻字段防止委托被 GC
    private readonly IntPtr _prevWndProc;

    private IntPtr _memDc;
    private IntPtr _hBitmap;
    private IntPtr _oldBitmap;
    private int _width, _height;

    private System.Windows.Forms.Timer? _closeTimer;
    private System.Windows.Forms.Timer? _fadeTimer;
    private int _alpha = 255;
    private bool _disposed;

    private SplashWindow()
    {
        _wndProcDelegate = WndProcHook;
        _hwnd = GlowInterop.CreateWindowEx(
            GlowInterop.WS_EX_LAYERED | GlowInterop.WS_EX_TRANSPARENT
            | GlowInterop.WS_EX_NOACTIVATE | GlowInterop.WS_EX_TOOLWINDOW,
            "STATIC", WindowTitle, GlowInterop.WS_POPUP,
            0, 0, 0, 0, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
        if (_hwnd == IntPtr.Zero)
            throw new InvalidOperationException("创建启动界面窗口失败");

        // 子类化:命中测试一律返回 HTTRANSPARENT,鼠标完全穿透
        _prevWndProc = GlowInterop.SetWindowLongPtr(
            _hwnd, GlowInterop.GWLP_WNDPROC,
            Marshal.GetFunctionPointerForDelegate(_wndProcDelegate));
    }

    /// <summary>创建并显示启动界面;资源缺失/绘制失败返回 null,不阻断启动。</summary>
    public static SplashWindow? TryShow()
    {
        try
        {
            using var stream = Assembly.GetExecutingAssembly()
                .GetManifestResourceStream("Hauteur.Assets.NewIcon.png");
            if (stream is null) return null;
            using var logo = new Bitmap(stream);

            // 显示宽度 280,高度按比例(NewIcon 接近方形)
            int dw = 280, dh = Math.Max(1, (int)Math.Round(280f * logo.Height / logo.Width));
            using var bmp = new Bitmap(dw, dh, PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(bmp))
            {
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.DrawImage(logo, 0, 0, dw, dh);
            }

            // 读取像素(GDI+ ARGB 未预乘)并预乘为 BGRA
            var rect = new Rectangle(0, 0, dw, dh);
            var data = bmp.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
            int stride = Math.Abs(data.Stride);
            var pixels = new byte[stride * dh];
            Marshal.Copy(data.Scan0, pixels, 0, pixels.Length);
            bmp.UnlockBits(data);
            Premultiply(pixels, stride, dw, dh);

            var splash = new SplashWindow();
            splash.InitBitmap(pixels, stride, dw, dh);

            // 居中于主屏
            var screen = Screen.PrimaryScreen?.Bounds ?? new Rectangle(0, 0, 1920, 1080);
            int x = screen.X + (screen.Width - dw) / 2;
            int y = screen.Y + (screen.Height - dh) / 2;
            GlowInterop.SetWindowPos(
                splash._hwnd, NativeMethods.HWND_TOPMOST, x, y, dw, dh,
                SWP_NOACTIVATE | GlowInterop.SWP_SHOWWINDOW);
            splash.Repaint(255);
            return splash;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>visibleDuration 毫秒后开始淡出(fadeDuration 毫秒),全程不抢焦点。</summary>
    public void CloseAfter(TimeSpan visibleDuration, TimeSpan fadeDuration)
    {
        _closeTimer = new System.Windows.Forms.Timer
        {
            Interval = Math.Max(1, (int)visibleDuration.TotalMilliseconds),
        };
        _closeTimer.Tick += (_, _) =>
        {
            _closeTimer.Stop();
            _closeTimer.Dispose();
            _closeTimer = null;
            BeginFade(fadeDuration);
        };
        _closeTimer.Start(); // 消息循环启动后开始计时
    }

    private void InitBitmap(byte[] pixels, int stride, int width, int height)
    {
        var bmi = new GlowInterop.BITMAPINFO
        {
            bmiHeader = new GlowInterop.BITMAPINFOHEADER
            {
                biSize = (uint)Marshal.SizeOf<GlowInterop.BITMAPINFOHEADER>(),
                biWidth = width,
                biHeight = -height,
                biPlanes = 1,
                biBitCount = 32,
                biCompression = 0,
            },
        };
        _memDc = GlowInterop.CreateCompatibleDC(IntPtr.Zero);
        _hBitmap = GlowInterop.CreateDIBSection(_memDc, ref bmi, GlowInterop.DIB_RGB_COLORS, out var bits, IntPtr.Zero, 0);
        Marshal.Copy(pixels, 0, bits, pixels.Length);
        _oldBitmap = GlowInterop.SelectObject(_memDc, _hBitmap);
        _width = width;
        _height = height;
    }

    private void BeginFade(TimeSpan fadeDuration)
    {
        int steps = Math.Max(1, (int)(fadeDuration.TotalMilliseconds / FadeStepMs));
        _fadeTimer = new System.Windows.Forms.Timer { Interval = FadeStepMs };
        _fadeTimer.Tick += (_, _) =>
        {
            _alpha -= 255 / steps;
            if (_alpha <= 0)
            {
                _fadeTimer.Stop();
                Destroy();
                return;
            }
            Repaint(Math.Clamp(_alpha, 0, 255));
        };
        _fadeTimer.Start();
    }

    /// <summary>用当前整体透明度重绘(位图不变,只调 SourceConstantAlpha)。</summary>
    private void Repaint(int alpha)
    {
        if (_disposed) return;
        var size = new GlowInterop.SIZE { cx = _width, cy = _height };
        var ptSrc = new GlowInterop.POINT();
        var blend = new GlowInterop.BLENDFUNCTION
        {
            BlendOp = 0,
            SourceConstantAlpha = (byte)alpha,
            AlphaFormat = 1,
        };
        GlowInterop.UpdateLayeredWindow(_hwnd, IntPtr.Zero, IntPtr.Zero, ref size, _memDc, ref ptSrc, 0, ref blend, GlowInterop.ULW_ALPHA);
    }

    private IntPtr WndProcHook(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        if (msg == GlowInterop.WM_NCHITTEST) return (IntPtr)(-1); // HTTRANSPARENT
        return GlowInterop.CallWindowProc(_prevWndProc, hWnd, msg, wParam, lParam);
    }

    private void Destroy()
    {
        if (_disposed) return;
        _disposed = true;
        _closeTimer?.Dispose();
        _fadeTimer?.Dispose();
        GlowInterop.SetWindowLongPtr(_hwnd, GlowInterop.GWLP_WNDPROC, _prevWndProc);
        GlowInterop.SelectObject(_memDc, _oldBitmap);
        GlowInterop.DeleteObject(_hBitmap);
        GlowInterop.DeleteDC(_memDc);
        GlowInterop.DestroyWindow(_hwnd);
    }

    public void Dispose() => Destroy();

    /// <summary>把 GDI+ ARGB(未预乘,BGRA 字节序)原地转成 UpdateLayeredWindow 需要的预乘 BGRA。</summary>
    private static void Premultiply(byte[] pixels, int stride, int width, int height)
    {
        for (int y = 0; y < height; y++)
        {
            int row = y * stride;
            for (int x = 0; x < width; x++)
            {
                int i = row + x * 4;
                float a = pixels[i + 3] / 255f;
                pixels[i] = (byte)(pixels[i] * a);
                pixels[i + 1] = (byte)(pixels[i + 1] * a);
                pixels[i + 2] = (byte)(pixels[i + 2] * a);
            }
        }
    }
}

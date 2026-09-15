using System.Runtime.InteropServices;
using Hauteur.Interop;

namespace Hauteur.App;

/// <summary>
/// 单个光晕覆盖窗口:比目标窗口四周各大 <see cref="GlowWidth"/> 像素,
/// 用 UpdateLayeredWindow 以每像素 alpha 绘制圆角渐变描边(贴近窗口处最亮、向外渐隐、中心全透明)。
/// 覆盖窗口始终置顶、鼠标完全穿透(WM_NCHITTEST 返回 HTTRANSPARENT)、不激活、不占任务栏。
/// </summary>
internal sealed class GlowOverlay : IDisposable
{
    internal const int GlowWidth = 8;

    /// <summary>覆盖窗口标题,测试/排障时按此识别。</summary>
    internal const string WindowTitle = "Hauteur.Glow";

    private const uint SWP_NOACTIVATE = 0x0010;

    private readonly IntPtr _hwnd;
    private readonly GlowInterop.WndProc _wndProcDelegate; // 常驻字段防止委托被 GC
    private readonly IntPtr _prevWndProc;

    private int _lastX = int.MinValue, _lastY = int.MinValue, _lastW, _lastH;
    private int _bmpWidth = -1, _bmpHeight = -1;
    private uint _argb;
    private float _opacity;
    private int _cornerRadius = -1;
    private bool _hidden = true;
    private readonly int _bandStart; // 环带内缘到窗口轮廓的距离:层级光晕 0,窗口组光晕为 GlowWidth(外圈,与层级光晕并存)

    /// <param name="bandStart">环带内缘到窗口轮廓的距离(px),0 = 紧贴窗口边缘。</param>
    public GlowOverlay(int bandStart = 0)
    {
        _bandStart = bandStart;
        _wndProcDelegate = WndProcHook;
        _hwnd = GlowInterop.CreateWindowEx(
            GlowInterop.WS_EX_LAYERED | GlowInterop.WS_EX_TRANSPARENT
            | GlowInterop.WS_EX_NOACTIVATE | GlowInterop.WS_EX_TOOLWINDOW,
            "STATIC", WindowTitle, GlowInterop.WS_POPUP,
            0, 0, 0, 0, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
        if (_hwnd == IntPtr.Zero)
            throw new InvalidOperationException("创建光晕覆盖窗口失败");

        // 子类化:命中测试一律返回 HTTRANSPARENT,保证鼠标完全穿透光晕
        _prevWndProc = GlowInterop.SetWindowLongPtr(
            _hwnd, GlowInterop.GWLP_WNDPROC,
            Marshal.GetFunctionPointerForDelegate(_wndProcDelegate));
    }

    private IntPtr WndProcHook(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        if (msg == GlowInterop.WM_NCHITTEST) return (IntPtr)(-1); // HTTRANSPARENT
        return GlowInterop.CallWindowProc(_prevWndProc, hWnd, msg, wParam, lParam);
    }

    /// <summary>
    /// 同步光晕:目标不可见/最小化时隐藏;按目标可见边界(DWM)定位;
    /// Z 序上始终紧跟目标窗口之后——比目标靠前的窗口会自然遮挡光晕,不会"透层"显示。
    /// </summary>
    public void Sync(IntPtr target, uint argb, float opacity)
    {
        if (!NativeMethods.IsWindow(target) || !GetVisibleBounds(target, out var r))
        {
            Hide();
            return;
        }

        // 目标隐藏或最小化时,光晕一并隐藏
        if (!NativeMethods.IsWindowVisible(target) || GlowInterop.IsIconic(target))
        {
            Hide();
            return;
        }

        int w = r.Right - r.Left;
        int h = r.Bottom - r.Top;
        _lastX = r.Left; _lastY = r.Top; _lastW = w; _lastH = h;

        // 每次都重新插入到目标窗口正后方(SetWindowPos 的 insertAfter=target),
        // 同时完成重定位、显示与 Z 序修正;SWP_NOACTIVATE 不抢焦点
        int expand = _bandStart + GlowWidth;
        uint flags = SWP_NOACTIVATE;
        if (_hidden) flags |= GlowInterop.SWP_SHOWWINDOW;
        GlowInterop.SetWindowPos(
            _hwnd, target,
            r.Left - expand, r.Top - expand, w + expand * 2, h + expand * 2,
            flags);
        _hidden = false;

        int corner = GetCornerRadius(target);
        int bw = w + expand * 2;
        int bh = h + expand * 2;
        if (_bmpWidth != bw || _bmpHeight != bh || _argb != argb || _cornerRadius != corner || !_opacity.Equals(opacity))
            Redraw(bw, bh, argb, opacity, corner);
    }

    /// <summary>读取窗口真实圆角半径(与 DWM 渲染一致):偏好 1→0,3→4px,其余/默认→8px;读取失败按直角。</summary>
    private static int GetCornerRadius(IntPtr target)
    {
        int hr = GlowInterop.DwmGetWindowAttributeInt(
            target, GlowInterop.DWMWA_WINDOW_CORNER_PREFERENCE, out int pref, sizeof(int));
        if (hr != 0) return 0;
        return pref switch { 1 => 0, 3 => 4, _ => 8 };
    }

    /// <summary>取目标窗口可见边界:优先 DWM EXTENDED_FRAME_BOUNDS,失败退回 GetWindowRect。</summary>
    private static bool GetVisibleBounds(IntPtr target, out GlowInterop.RECT r)
    {
        r = default;
        int hr = GlowInterop.DwmGetWindowAttribute(
            target, GlowInterop.DWMWA_EXTENDED_FRAME_BOUNDS,
            out r, (uint)Marshal.SizeOf<GlowInterop.RECT>());
        if (hr == 0) return true;
        return GlowInterop.GetWindowRect(target, out r);
    }

    public void Hide()
    {
        if (_hidden) return;
        GlowInterop.ShowWindow(_hwnd, GlowInterop.SW_HIDE);
        _hidden = true;
    }

    public void Dispose()
    {
        GlowInterop.SetWindowLongPtr(_hwnd, GlowInterop.GWLP_WNDPROC, _prevWndProc); // 恢复原窗口过程
        GlowInterop.DestroyWindow(_hwnd);
    }

    /// <summary>
    /// 重绘渐变描边:以窗口轮廓(圆角半径与 DWM 一致)的等距偏移为环带,
    /// 窗口边缘最亮 → 向外渐隐;环带与窗口圆角严格同心,保证光晕 R 角匹配窗口 R 角。
    /// </summary>
    private void Redraw(int w, int h, uint argb, float opacity, int cornerRadius)
    {
        byte rC = (byte)(argb >> 16), gC = (byte)(argb >> 8), bC = (byte)argb;
        var pixels = new byte[w * h * 4];

        // 窗口形状的圆角矩形 SDF(半宽高减去圆角半径):窗口边缘 = 距离 0,
        // 光晕环带 = 距离 [_bandStart, _bandStart + GlowWidth]
        float bx = w * 0.5f - cornerRadius, by = h * 0.5f - cornerRadius;
        float r = cornerRadius;
        int edge = (_bandStart + GlowWidth) * 2; // 扫描边距:覆盖圆角区域的向外偏移(圆角 ≤ 8px + 环带)

        // 仅扫描边缘带(上下 edge 行整行 + 中部行左右 edge 列),其余区域必然全透明
        float centerY = h * 0.5f;
        for (int y = 0; y < h; y++)
        {
            float py = y + 0.5f - centerY;
            bool edgeRow = y < edge || y >= h - edge;

            if (edgeRow)
            {
                DrawRow(pixels, w, y, py, 0, w, bx, by, r, rC, gC, bC, opacity);
            }
            else
            {
                DrawRow(pixels, w, y, py, 0, Math.Min(edge, w), bx, by, r, rC, gC, bC, opacity);
                if (w > edge)
                    DrawRow(pixels, w, y, py, w - edge, w, bx, by, r, rC, gC, bC, opacity);
            }
        }

        // 创建 32bpp 自顶向下 DIB,交给 UpdateLayeredWindow(ULW_ALPHA 逐像素 alpha)
        var bmi = new GlowInterop.BITMAPINFO
        {
            bmiHeader = new GlowInterop.BITMAPINFOHEADER
            {
                biSize = (uint)Marshal.SizeOf<GlowInterop.BITMAPINFOHEADER>(),
                biWidth = w,
                biHeight = -h, // 负值 = 自顶向下
                biPlanes = 1,
                biBitCount = 32,
                biCompression = 0, // BI_RGB
            },
        };

        var dc = GlowInterop.CreateCompatibleDC(IntPtr.Zero);
        var hBmp = GlowInterop.CreateDIBSection(dc, ref bmi, GlowInterop.DIB_RGB_COLORS, out var bits, IntPtr.Zero, 0);
        Marshal.Copy(pixels, 0, bits, pixels.Length);
        var old = GlowInterop.SelectObject(dc, hBmp);

        var size = new GlowInterop.SIZE { cx = w, cy = h };
        var ptSrc = new GlowInterop.POINT();
        var blend = new GlowInterop.BLENDFUNCTION
        {
            BlendOp = 0,                   // AC_SRC_OVER
            SourceConstantAlpha = 255,
            AlphaFormat = 1,               // AC_SRC_ALPHA(源位图已预乘)
        };
        GlowInterop.UpdateLayeredWindow(_hwnd, IntPtr.Zero, IntPtr.Zero, ref size, dc, ref ptSrc, 0, ref blend, GlowInterop.ULW_ALPHA);

        GlowInterop.SelectObject(dc, old);
        GlowInterop.DeleteObject(hBmp);
        GlowInterop.DeleteDC(dc);

        _bmpWidth = w; _bmpHeight = h; _argb = argb; _opacity = opacity; _cornerRadius = cornerRadius;
    }

    /// <summary>计算一行 [x0, x1) 内的光晕像素(BGRA 预乘)。</summary>
    /// <remarks>
    /// 环带 = 到窗口轮廓(圆角矩形,半径与 DWM 一致)的距离 ∈ [bandStart, bandStart + GlowWidth]:
    /// 环带内缘最亮,向外平滑渐隐;窗口内部同样有环带但被窗口自身遮挡,无需剔除。
    /// </remarks>
    private void DrawRow(
        byte[] pixels, int w, int y, float py, int x0, int x1,
        float bx, float by, float r,
        byte rC, byte gC, byte bC, float opacity)
    {
        float centerX = w * 0.5f;
        for (int x = x0; x < x1; x++)
        {
            float px = x + 0.5f - centerX;
            float dist = MathF.Abs(SdRoundRect(px, py, bx, by, r)); // 到窗口轮廓的距离

            float t = (dist - _bandStart) / GlowWidth;
            if (t < 0f || t >= 1f) continue;

            float s = 1f - t;
            s = s * s * (3f - 2f * s); // smoothstep,渐变更柔和
            float alpha = s * opacity;
            if (alpha <= 0.003f) continue;

            int i = (y * w + x) * 4;
            pixels[i] = (byte)(bC * alpha);
            pixels[i + 1] = (byte)(gC * alpha);
            pixels[i + 2] = (byte)(rC * alpha);
            pixels[i + 3] = (byte)(alpha * 255f);
        }
    }

    /// <summary>圆角矩形有符号距离场(SDF):负值在形内。</summary>
    private static float SdRoundRect(float px, float py, float bx, float by, float r)
    {
        float qx = MathF.Abs(px) - bx + r;
        float qy = MathF.Abs(py) - by + r;
        float ox = MathF.Max(qx, 0f), oy = MathF.Max(qy, 0f);
        return MathF.Min(MathF.Max(qx, qy), 0f) + MathF.Sqrt(ox * ox + oy * oy) - r;
    }
}

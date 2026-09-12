using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Reflection;

namespace TOPPEUR.App;

/// <summary>
/// 托盘/窗口图标:从嵌入资源 Assets\logo.ico(由 logo.png 生成)加载;
/// 资源缺失或损坏时用简单绘制的占位图标兜底,保证托盘不空白。
/// </summary>
internal static class AppIcon
{
    private static readonly Lazy<Icon> LazyIcon = new(CreateInternal);

    /// <summary>返回图标的克隆,调用方各自 Dispose,互不影响。</summary>
    public static Icon Create() => (Icon)LazyIcon.Value.Clone();

    private static Icon CreateInternal()
    {
        try
        {
            using var stream = Assembly.GetExecutingAssembly()
                .GetManifestResourceStream("TOPPEUR.Assets.logo.ico");
            if (stream is not null) return new Icon(stream);
        }
        catch
        {
            // 资源缺失/损坏时走占位图标
        }
        return CreatePlaceholder();
    }

    private static Icon CreatePlaceholder()
    {
        using var bmp = new Bitmap(32, 32, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;

            using var backBrush = new SolidBrush(Color.FromArgb(70, 84, 106));
            FillRoundRect(g, backBrush, new Rectangle(2, 9, 17, 19), 4);

            using var frontBrush = new SolidBrush(Color.FromArgb(0, 168, 255));
            FillRoundRect(g, frontBrush, new Rectangle(13, 4, 17, 19), 4);

            using var arrowPen = new Pen(Color.White, 3f)
            {
                StartCap = LineCap.Round,
                EndCap = LineCap.Round,
                LineJoin = LineJoin.Round,
            };
            g.DrawLines(arrowPen, new[]
            {
                new PointF(21.5f, 25f),
                new PointF(21.5f, 12f),
                new PointF(17.5f, 16f),
            });
        }

        var hIcon = bmp.GetHicon();
        try
        {
            return (Icon)Icon.FromHandle(hIcon).Clone();
        }
        finally
        {
            DestroyIcon(hIcon);
        }
    }

    [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static extern bool DestroyIcon(IntPtr hIcon);

    private static void FillRoundRect(Graphics g, Brush brush, Rectangle rect, int radius)
    {
        using var path = new GraphicsPath();
        int d = radius * 2;
        path.AddArc(rect.X, rect.Y, d, d, 180, 90);
        path.AddArc(rect.Right - d, rect.Y, d, d, 270, 90);
        path.AddArc(rect.Right - d, rect.Bottom - d, d, d, 0, 90);
        path.AddArc(rect.X, rect.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        g.FillPath(brush, path);
    }
}

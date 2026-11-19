using System.Windows;
using System.Windows.Media;

namespace BetterGenshinImpact.View.Controls;

/// <summary>
/// 地图点位图标的统一画法：深色圆底 + 细描边，图标按比例内缩在圆内。
/// <para>
/// 之前是把整张图标裁成圆形、铺满 32px，图标边缘被切掉，看起来比游戏原生地图大一圈。
/// 原生的图标在圆底里四周留白，方形贴图的四角也落在圆内，所以这里统一内缩到 0.68 倍直径
/// （圆内接正方形边长约为直径的 0.707 倍，取 0.68 留一点余量），不再裁切。
/// </para>
/// <para>
/// 大地图 <see cref="PointsCanvas"/> 与小地图 <see cref="MiniMapPointsCanvas"/> 共用这一份，
/// 两处尺寸或配色要改时只改这里，避免出现"大地图大、小地图小"的割裂。
/// </para>
/// </summary>
internal static class MaskMapPointIconRenderer
{
    /// <summary>
    /// 图标在圆内占的比例。原生的图标四周留白约三成。
    /// </summary>
    private const double IconScale = 0.68;

    /// <summary>
    /// 圆底填充色，与游戏内地图标记一致。
    /// </summary>
    private static readonly Brush FillBrush = CreateBrush("#323947");

    /// <summary>
    /// 描边色，取原神 UI 的金色描边。
    /// </summary>
    private static readonly Brush BorderBrush = CreateBrush("#D3BC8E");

    /// <summary>
    /// 投影色，让图标在大地图底图上不糊成一片。
    /// </summary>
    private static readonly Brush ShadowBrush = CreateBrush("#1E000000");

    /// <summary>
    /// 描边宽度。
    /// </summary>
    private const double StrokeThickness = 2.0;

    /// <summary>
    /// 画一个点位。
    /// </summary>
    /// <param name="dc">绘制上下文。</param>
    /// <param name="centerX">圆心屏幕 X。</param>
    /// <param name="centerY">圆心屏幕 Y。</param>
    /// <param name="size">圆的直径，通常为 32。</param>
    /// <param name="icon">图标；为空时用 <paramref name="fallback"/> 填一个小圆点。</param>
    /// <param name="fallback">没有图标时的替代色。</param>
    public static void Draw(DrawingContext dc, double centerX, double centerY, double size, ImageSource? icon, Brush fallback)
    {
        var radius = size / 2.0;
        var center = new Point(centerX, centerY);

        // 投影偏移固定 2px，缩放地图时也不会变粗
        dc.DrawGeometry(ShadowBrush, null, new EllipseGeometry(new Point(center.X + 2, center.Y + 2), radius, radius));
        dc.DrawGeometry(FillBrush, new Pen(BorderBrush, StrokeThickness), new EllipseGeometry(center, radius, radius));

        if (icon == null)
        {
            // 图标还没下载完：先给个纯色小圆点，加载完成后画布会收到 ImageUpdated 重绘
            var dotRadius = radius * IconScale / 2.0;
            dc.DrawEllipse(fallback, null, center, dotRadius, dotRadius);
            return;
        }

        var iconSize = size * IconScale;
        var iconRect = new Rect(center.X - iconSize / 2.0, center.Y - iconSize / 2.0, iconSize, iconSize);
        dc.DrawImage(icon, iconRect);
    }

    /// <summary>
    /// 建一个冻结的画刷，跨线程绘制也安全。
    /// </summary>
    private static Brush CreateBrush(string color)
    {
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(color));
        brush.Freeze();
        return brush;
    }
}

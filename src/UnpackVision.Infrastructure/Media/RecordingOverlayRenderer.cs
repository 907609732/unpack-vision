using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using OpenCvSharp;
using UnpackVision.Core;

namespace UnpackVision.Infrastructure;

/// <summary>
/// Renders recording metadata onto a caller-owned OpenCV frame without
/// retaining the frame or its native pointer after the call.
/// </summary>
internal static class RecordingOverlayRenderer
{
    internal static void Draw(
        Mat frame,
        RecordingSession session,
        IReadOnlyList<RecordTagAssignment> issueTags)
    {
        var scale = Math.Max(0.8, frame.Width / 1920d);
        var thickness = Math.Max(2, (int)Math.Round(scale * 2));
        var x = Math.Max(18, frame.Width / 100);
        var y = Math.Max(42, frame.Height / 22);
        DrawOutlinedText(frame, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"), new Point(x, y), scale, thickness);
        DrawChineseText(
            frame,
            $"快递单号：{session.TrackingNo}",
            x,
            y + (int)(12 * scale),
            scale,
            thickness,
            System.Drawing.Color.White);

        var line = 0;
        foreach (var tag in issueTags.Where(item => item.IsActive).Take(4))
        {
            DrawChineseText(
                frame,
                $"异常：{tag.TagName} {tag.TaggedAt.LocalDateTime:HH:mm:ss}",
                x,
                y + (int)((52 + line * 36) * scale),
                scale * 0.88,
                thickness,
                System.Drawing.Color.FromArgb(255, 255, 75, 75));
            line++;
        }
    }

    private static void DrawChineseText(
        Mat frame,
        string text,
        int x,
        int y,
        double scale,
        int thickness,
        System.Drawing.Color fillColor)
    {
        if (frame.Type() != MatType.CV_8UC3 || frame.Empty())
        {
            DrawOutlinedText(frame, text, new Point(x, y + (int)(28 * scale)), scale, thickness);
            return;
        }
        try
        {
            using var bitmap = new System.Drawing.Bitmap(
                frame.Width,
                frame.Height,
                checked((int)frame.Step()),
                PixelFormat.Format24bppRgb,
                frame.Data);
            using var graphics = System.Drawing.Graphics.FromImage(bitmap);
            graphics.SmoothingMode = SmoothingMode.AntiAlias;
            graphics.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
            using var family = new System.Drawing.FontFamily("Microsoft YaHei UI");
            using var path = new GraphicsPath();
            path.AddString(
                text,
                family,
                (int)System.Drawing.FontStyle.Bold,
                (float)(27 * scale),
                new System.Drawing.PointF(x, y),
                System.Drawing.StringFormat.GenericDefault);
            using var outline = new System.Drawing.Pen(System.Drawing.Color.Black, Math.Max(3, thickness + 2))
            {
                LineJoin = LineJoin.Round
            };
            graphics.DrawPath(outline, path);
            using var fill = new System.Drawing.SolidBrush(fillColor);
            graphics.FillPath(fill, path);
            graphics.Flush();
        }
        catch (Exception ex) when (ex is ArgumentException or PlatformNotSupportedException)
        {
            DrawOutlinedText(frame, text, new Point(x, y + (int)(28 * scale)), scale, thickness);
        }
    }

    private static void DrawOutlinedText(Mat frame, string text, Point origin, double scale, int thickness)
    {
        Cv2.PutText(frame, text, origin, HersheyFonts.HersheySimplex, scale, Scalar.Black, thickness + 3, LineTypes.AntiAlias);
        Cv2.PutText(frame, text, origin, HersheyFonts.HersheySimplex, scale, Scalar.White, thickness, LineTypes.AntiAlias);
    }
}

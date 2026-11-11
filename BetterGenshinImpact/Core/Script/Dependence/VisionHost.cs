using System;
using System.Collections.Generic;
using System.Linq;
using BetterGenshinImpact.Core.Recognition.OCR;
using BetterGenshinImpact.GameTask.Common;
using BetterGenshinImpact.GameTask.Model.Area;
using BetterGenshinImpact.GameTask.Runtime;
using OpenCvSharp;

namespace BetterGenshinImpact.Core.Script.Dependence;

/// <summary>一行 OCR 结果，坐标是相对截图区域的像素。</summary>
public sealed record OcrLine(string Text, double X, double Y, double Width, double Height, float Score);

/// <summary>任务追踪面板的解析结果。</summary>
public sealed record QuestTracker(string Title, string Step, string RawText, IReadOnlyList<OcrLine> Lines);

/// <summary>
/// OCR 宿主。把屏幕或任意区域转成文字，供脚本做条件判断。
/// <para>坐标默认按 1080P 基准书写，内部按当前窗口尺寸自动换算；传 use1080P = false 则按实际像素处理。</para>
/// </summary>
public sealed class OcrHost
{
    private readonly GameRuntime _runtime;

    public OcrHost(GameRuntime runtime)
    {
        _runtime = runtime;
    }

    /// <summary>当前截图的宽（实际像素）。</summary>
    public int Width => _runtime.SystemInfo.CaptureAreaRect.Width;

    /// <summary>当前截图的高（实际像素）。</summary>
    public int Height => _runtime.SystemInfo.CaptureAreaRect.Height;

    /// <summary>相对 1080P 的缩放比例。</summary>
    public double ScaleTo1080P => _runtime.SystemInfo.ScaleTo1080PRatio;

    /// <summary>识别整屏，返回按阅读顺序拼接的文本。</summary>
    public string ReadScreen() => ReadRegion(0, 0, 0, 0);

    /// <summary>
    /// 识别指定区域，返回拼接文本。
    /// </summary>
    /// <param name="x">左上角 X，默认按 1080P 基准</param>
    /// <param name="y">左上角 Y，默认按 1080P 基准</param>
    /// <param name="w">宽，0 表示到屏幕边缘</param>
    /// <param name="h">高，0 表示到屏幕边缘</param>
    /// <param name="use1080P">true 时把入参当作 1080P 基准坐标自动缩放</param>
    public string ReadRegion(int x, int y, int w, int h, bool use1080P = true)
    {
        var result = Recognize(x, y, w, h, use1080P);
        return result.Text;
    }

    /// <summary>
    /// 识别指定区域，返回逐行结果（带坐标与置信度），便于按位置区分标题与正文。
    /// </summary>
    public OcrLine[] ReadLines(int x = 0, int y = 0, int w = 0, int h = 0, bool use1080P = true)
    {
        var result = Recognize(x, y, w, h, use1080P);
        return result.Regions
            .OrderBy(r => r.Rect.Center.Y)
            .ThenBy(r => r.Rect.Center.X)
            .Select(r =>
            {
                var box = r.Rect.BoundingRect();
                return new OcrLine(r.Text, box.X, box.Y, box.Width, box.Height, r.Score);
            })
            .ToArray();
    }

    /// <summary>
    /// 识别并直接把区域存成图片，便于调区域参数。路径相对脚本目录。
    /// </summary>
    public bool SaveRegion(string path, int x = 0, int y = 0, int w = 0, int h = 0, bool use1080P = true)
    {
        using var region = Crop(x, y, w, h, use1080P);
        return Cv2.ImWrite(path, region.SrcMat);
    }

    internal ImageRegion Crop(int x, int y, int w, int h, bool use1080P)
    {
        var full = _runtime.CaptureToRectArea();
        if (!use1080P)
        {
            return CropCore(full, x, y, w, h);
        }

        var ratio = ScaleTo1080P <= 0 ? 1 : ScaleTo1080P;
        return CropCore(full,
            (int)Math.Round(x * ratio), (int)Math.Round(y * ratio),
            (int)Math.Round(w * ratio), (int)Math.Round(h * ratio));
    }

    private static ImageRegion CropCore(ImageRegion full, int x, int y, int w, int h)
    {
        var fullWidth = full.SrcMat.Cols;
        var fullHeight = full.SrcMat.Rows;
        var width = w <= 0 ? fullWidth - x : Math.Min(w, fullWidth - x);
        var height = h <= 0 ? fullHeight - y : Math.Min(h, fullHeight - y);
        x = Math.Clamp(x, 0, fullWidth - 1);
        y = Math.Clamp(y, 0, fullHeight - 1);
        width = Math.Clamp(width, 1, fullWidth - x);
        height = Math.Clamp(height, 1, fullHeight - y);
        return full.DeriveCrop(x, y, width, height);
    }

    private OcrResult Recognize(int x, int y, int w, int h, bool use1080P)
    {
        using var region = Crop(x, y, w, h, use1080P);
        return RecognizeMat(region.SrcMat);
    }

    internal static OcrResult RecognizeMat(Mat mat)
    {
        try
        {
            return OcrFactory.Paddle.OcrResult(mat);
        }
        catch (Exception e)
        {
            throw new InvalidOperationException("OCR 引擎不可用，请先在 BetterGI 设置中下载并选择 OCR 模型。", e);
        }
    }
}

/// <summary>
/// 任务追踪宿主：读取左上角任务面板的标题与当前步骤。
/// <para>区域按窗口比例计算，不依赖固定分辨率；识别不准时用 Tracker(x, y, w, h) 手动指定 1080P 基准区域。</para>
/// </summary>
public sealed class QuestHost
{
    private readonly OcrHost _ocr;

    public QuestHost(OcrHost ocr)
    {
        _ocr = ocr;
    }

    /// <summary>默认区域（1080P 基准）：左上角任务追踪面板。</summary>
    private static readonly Rect DefaultArea = new(28, 118, 430, 190);

    /// <summary>读取任务追踪面板。</summary>
    public QuestTracker Tracker()
    {
        var ratio = _ocr.ScaleTo1080P <= 0 ? 1 : _ocr.ScaleTo1080P;
        return Tracker(
            (int)Math.Round(DefaultArea.X * ratio), (int)Math.Round(DefaultArea.Y * ratio),
            (int)Math.Round(DefaultArea.Width * ratio), (int)Math.Round(DefaultArea.Height * ratio),
            use1080P: false);
    }

    /// <summary>读取指定区域内的任务追踪面板。</summary>
    public QuestTracker Tracker(int x, int y, int w, int h, bool use1080P = true)
    {
        var lines = _ocr.ReadLines(x, y, w, h, use1080P);
        if (lines.Length == 0)
        {
            return new QuestTracker(string.Empty, string.Empty, string.Empty, lines);
        }

        var merged = MergeLines(lines);
        // 标题字号明显大于正文，取行高最大的那行；没有明显差异时退化为第一行
        var maxHeight = merged.Max(l => l.Height);
        var titleIndex = merged.FindIndex(l => l.Height >= maxHeight * 0.95);
        if (titleIndex < 0)
        {
            titleIndex = 0;
        }

        var title = merged[titleIndex].Text;
        var step = string.Join(" ", merged.Where((_, i) => i != titleIndex).Select(l => l.Text)).Trim();
        var raw = string.Join("\n", merged.Select(l => l.Text));
        return new QuestTracker(title, step, raw, lines);
    }

    /// <summary>把 Y 接近的文本块合并成一行，避免标题被拆成多段。</summary>
    private static List<OcrLine> MergeLines(OcrLine[] lines)
    {
        var merged = new List<OcrLine>();
        foreach (var line in lines.OrderBy(l => l.Y).ThenBy(l => l.X))
        {
            if (merged.Count > 0)
            {
                var last = merged[^1];
                var centerGap = Math.Abs((last.Y + last.Height / 2) - (line.Y + line.Height / 2));
                if (centerGap <= Math.Max(last.Height, line.Height) * 0.6)
                {
                    merged[^1] = last with
                    {
                        Text = last.Text + line.Text,
                        X = Math.Min(last.X, line.X),
                        Y = Math.Min(last.Y, line.Y),
                        Width = Math.Max(last.X + last.Width, line.X + line.Width) - Math.Min(last.X, line.X),
                        Height = Math.Max(last.Height, line.Height),
                        Score = Math.Min(last.Score, line.Score),
                    };
                    continue;
                }
            }

            merged.Add(line);
        }

        return merged;
    }
}

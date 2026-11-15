using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BetterGenshinImpact.Core.Config;
using BetterGenshinImpact.Core.Input;
using BetterGenshinImpact.Core.Recognition.OCR;
using BetterGenshinImpact.Core.Simulator.Extensions;
using BetterGenshinImpact.GameTask;
using BetterGenshinImpact.GameTask.AutoDomain.Model;
using BetterGenshinImpact.GameTask.Common;
using BetterGenshinImpact.GameTask.Runtime;
using BetterGenshinImpact.Service.Interface;
using Microsoft.Extensions.Logging;
using OpenCvSharp;
using Vanara.PInvoke;

namespace BetterGenshinImpact.Service.RemoteTask;

/// <summary>
/// 一次树脂读数。
/// </summary>
/// <param name="Original">原粹树脂。</param>
/// <param name="Condensed">浓缩树脂。</param>
/// <param name="Max">原粹树脂上限。</param>
/// <param name="Source">数据来源，用于在回复里说明可信度。</param>
public sealed record ResinSnapshot(int Original, int Condensed, int Max, string Source)
{
    /// <summary>
    /// 距离满树脂还差多少。
    /// </summary>
    public int Missing => Math.Max(0, Max - Original);

    /// <summary>
    /// 按 8 分钟恢复 1 点估算，还要多久回满。
    /// </summary>
    public TimeSpan TimeToFull => TimeSpan.FromMinutes(Missing * 8.0);

    /// <summary>
    /// 一句话描述，聊天直接发这个。
    /// </summary>
    public override string ToString()
    {
        var condensed = Condensed > 0 ? $"，浓缩 {Condensed} 个" : string.Empty;
        var wait = Missing == 0
            ? "已满"
            : $"还差 {Missing} 点，约 {FormatWait(TimeToFull)} 回满";
        return $"原粹树脂 {Original}/{Max}{condensed}；{wait}（来源：{Source}）";
    }

    /// <summary>
    /// 把时长写成「3 小时 12 分」这种口语形式。
    /// </summary>
    /// <param name="span">时长。</param>
    public static string FormatWait(TimeSpan span)
    {
        if (span <= TimeSpan.Zero)
        {
            return "0 分钟";
        }

        var hours = (int)span.TotalHours;
        var minutes = span.Minutes;
        return hours > 0 ? $"{hours} 小时 {minutes} 分" : $"{minutes} 分钟";
    }
}

/// <summary>
/// 原子游戏操作：读树脂、关游戏、全屏截图、切人、开界面。
/// <para>
/// 这些动作不需要大模型决策，也不该走任务框架——它们一条命令就完成，
/// 因此单独抽出来，供聊天指令、定时任务、模型工具三处共用。
/// </para>
/// </summary>
public sealed class GameOps(
    IConfigService configService,
    GameRuntimeService runtimeService,
    MysDailyNoteClient mysClient,
    ILogger<GameOps> logger)
{
    /// <summary>
    /// 界面动作标识到游戏动作的映射。键是聊天里可以用的小写别名。
    /// </summary>
    private static readonly Dictionary<string, GIActions> Panels = new(StringComparer.OrdinalIgnoreCase)
    {
        ["map"] = GIActions.OpenMap,
        ["地图"] = GIActions.OpenMap,
        ["bag"] = GIActions.OpenInventory,
        ["inventory"] = GIActions.OpenInventory,
        ["背包"] = GIActions.OpenInventory,
        ["character"] = GIActions.OpenCharacterScreen,
        ["角色"] = GIActions.OpenCharacterScreen,
        ["wish"] = GIActions.OpenWishScreen,
        ["抽卡"] = GIActions.OpenWishScreen,
        ["祈愿"] = GIActions.OpenWishScreen,
        ["party"] = GIActions.OpenPartySetupScreen,
        ["队伍"] = GIActions.OpenPartySetupScreen,
        ["battlepass"] = GIActions.OpenBattlePassScreen,
        ["纪行"] = GIActions.OpenBattlePassScreen,
        ["quest"] = GIActions.OpenQuestMenu,
        ["任务"] = GIActions.OpenQuestMenu,
        ["menu"] = GIActions.OpenPaimonMenu,
        ["派蒙"] = GIActions.OpenPaimonMenu
    };

    /// <summary>
    /// 支持打开的界面别名，用于给模型与聊天提示。
    /// </summary>
    public static IReadOnlyCollection<string> PanelNames { get; } = Panels.Keys.ToArray();

    /// <summary>
    /// 读取树脂：配了米游社 Cookie 就直连官方接口，否则退回截屏 OCR。
    /// </summary>
    /// <param name="ct">取消令牌。</param>
    /// <returns>读数；两条路都不通时返回 null。</returns>
    public async Task<ResinSnapshot?> ReadResinAsync(CancellationToken ct = default)
    {
        var config = configService.Get().RemoteTaskConfig;
        if (!string.IsNullOrWhiteSpace(config.MysCookie))
        {
            try
            {
                var note = await mysClient.QueryAsync(config.MysCookie, config.MysUid, ct).ConfigureAwait(false);
                if (note != null)
                {
                    return new ResinSnapshot(note.CurrentResin, note.CondensedResin, note.MaxResin, "米游社");
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception e)
            {
                logger.LogWarning(e, "米游社查询树脂失败，改用截图识别");
            }
        }

        return ReadResinFromScreen(ct);
    }

    /// <summary>
    /// 从当前画面顶栏识别树脂。要求画面在主界面或战斗中（顶栏可见）。
    /// </summary>
    /// <param name="ct">取消令牌。</param>
    private ResinSnapshot? ReadResinFromScreen(CancellationToken ct)
    {
        try
        {
            var runtime = runtimeService.Current;
            if (runtime == null)
            {
                return null;
            }

            using var capture = runtime.CaptureToRectArea();
            var status = ResinStatus.RecogniseFromRegion(capture, runtime.SystemInfo, OcrFactory.Paddle);
            // 识别不到时计数是 0，直接当失败，别把 0 当成真实体力发出去
            if (status.OriginalResinCount <= 0)
            {
                return null;
            }

            return new ResinSnapshot(status.OriginalResinCount, status.CondensedResinCount, 200, "画面识别");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception e)
        {
            logger.LogDebug(e, "画面识别树脂失败");
            return null;
        }
    }

    /// <summary>
    /// 关闭游戏：先走运行时服务的正常关闭，再兜底结束同会话内的原神进程。
    /// </summary>
    /// <param name="ct">取消令牌。</param>
    /// <returns>是否在超时前退出。</returns>
    public async Task<bool> CloseGameAsync(CancellationToken ct = default)
    {
        var before = SystemControl.GetGenshinGameProcessNameList(null)
            .SelectMany(name => System.Diagnostics.Process.GetProcessesByName(name))
            .Select(p => p.Id)
            .ToArray();
        if (before.Length == 0)
        {
            logger.LogInformation("远程关闭游戏：当前没有运行中的原神进程");
            return true;
        }

        logger.LogInformation("远程关闭游戏：结束 {Count} 个进程", before.Length);
        try
        {
            runtimeService.CloseGame();
        }
        catch (Exception e)
        {
            logger.LogWarning(e, "运行时服务关闭游戏失败，改为直接结束进程");
            SystemControl.CloseGameProcesses(null);
        }

        for (var i = 0; i < 20 && !ct.IsCancellationRequested; i++)
        {
            await Task.Delay(500, ct).ConfigureAwait(false);
            var alive = before.Where(IsAlive).ToArray();
            if (alive.Length == 0)
            {
                return true;
            }
        }

        SystemControl.CloseGameProcesses(null);
        return before.All(id => !IsAlive(id));
    }

    /// <summary>
    /// 抓取整个桌面（含游戏窗口之外的区域），用于「全局截图」。
    /// </summary>
    /// <param name="maxWidth">最长边上限，0 表示原样。</param>
    /// <param name="quality">JPEG 质量。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>JPEG 字节；失败返回 null。</returns>
    public Task<byte[]?> CaptureDesktopAsync(int maxWidth, int quality, CancellationToken ct = default)
    {
        return Task.Run(() =>
        {
            try
            {
                var bounds = System.Windows.Forms.SystemInformation.VirtualScreen;
                if (bounds.Width <= 0 || bounds.Height <= 0)
                {
                    return null;
                }

                using var bitmap = new Bitmap(bounds.Width, bounds.Height, PixelFormat.Format24bppRgb);
                using (var graphics = Graphics.FromImage(bitmap))
                {
                    graphics.CopyFromScreen(bounds.X, bounds.Y, 0, 0, bitmap.Size, CopyPixelOperation.SourceCopy);
                }

                return Encode(bitmap, maxWidth, quality);
            }
            catch (Exception e)
            {
                logger.LogWarning(e, "全屏截图失败");
                return null;
            }
        }, ct);
    }

    /// <summary>
    /// 切换到队伍中的第 N 位角色（1–5）。
    /// </summary>
    /// <param name="index">角色位次，从 1 开始。</param>
    /// <param name="ct">取消令牌。</param>
    public async Task<bool> SwitchCharacterAsync(int index, CancellationToken ct = default)
    {
        if (index is < 1 or > 5)
        {
            return false;
        }

        var key = index switch
        {
            1 => User32.VK.VK_1,
            2 => User32.VK.VK_2,
            3 => User32.VK.VK_3,
            4 => User32.VK.VK_4,
            _ => User32.VK.VK_5
        };

        EnsureForeground();
        InputHub.Foreground.Keyboard.KeyPress(key);
        await Task.Delay(600, ct).ConfigureAwait(false);
        return true;
    }

    /// <summary>
    /// 打开一个游戏界面。
    /// </summary>
    /// <param name="panel">界面别名，见 <see cref="Panels"/>。</param>
    /// <param name="ct">取消令牌。</param>
    public async Task<bool> OpenPanelAsync(string panel, CancellationToken ct = default)
    {
        if (!Panels.TryGetValue(panel, out var action))
        {
            return false;
        }

        EnsureForeground();
        InputHub.Foreground.SimulateAction(action, KeyType.KeyPress);
        await Task.Delay(1200, ct).ConfigureAwait(false);
        return true;
    }

    /// <summary>
    /// 确保游戏窗口在前台。最小化或后台时先把窗口拉回来，否则按键发不到游戏里。
    /// </summary>
    public void EnsureForeground()
    {
        try
        {
            var runtime = runtimeService.Current;
            var window = runtime?.Window;
            if (window == null || !window.RequiresForeground || window.IsForeground)
            {
                return;
            }

            window.Activate();
        }
        catch (Exception e)
        {
            logger.LogDebug(e, "激活游戏窗口失败");
        }
    }

    /// <summary>
    /// 把界面别名翻译成游戏动作，供模型工具校验参数。
    /// </summary>
    /// <param name="panel">界面别名。</param>
    public static bool IsPanel(string panel) => Panels.ContainsKey(panel);

    /// <summary>
    /// 进程是否还活着。取不到进程（已退出或无权限）都按已退出处理。
    /// </summary>
    private static bool IsAlive(int processId)
    {
        try
        {
            using var process = System.Diagnostics.Process.GetProcessById(processId);
            return !process.HasExited;
        }
        catch (ArgumentException)
        {
            return false;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    /// <summary>
    /// 缩放并编码为 JPEG。
    /// </summary>
    private static byte[] Encode(Bitmap source, int maxWidth, int quality)
    {
        var longest = Math.Max(source.Width, source.Height);
        if (maxWidth <= 0 || longest <= maxWidth)
        {
            using var buffer = new MemoryStream();
            Save(source, buffer, quality);
            return buffer.ToArray();
        }

        var scale = (double)maxWidth / longest;
        var width = Math.Max(1, (int)Math.Round(source.Width * scale));
        var height = Math.Max(1, (int)Math.Round(source.Height * scale));
        using var resized = new Bitmap(source, width, height);
        using var output = new MemoryStream();
        Save(resized, output, quality);
        return output.ToArray();
    }

    /// <summary>
    /// 按指定质量保存 JPEG。
    /// </summary>
    private static void Save(Bitmap bitmap, Stream stream, int quality)
    {
        var encoder = ImageCodecInfo.GetImageEncoders().FirstOrDefault(c => c.FormatID == ImageFormat.Jpeg.Guid);
        if (encoder == null)
        {
            bitmap.Save(stream, ImageFormat.Jpeg);
            return;
        }

        using var parameters = new EncoderParameters(1);
        parameters.Param[0] = new EncoderParameter(System.Drawing.Imaging.Encoder.Quality, (long)Math.Clamp(quality, 1, 100));
        bitmap.Save(stream, encoder, parameters);
    }
}

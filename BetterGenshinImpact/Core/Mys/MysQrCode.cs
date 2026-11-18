using QRCoder;

namespace BetterGenshinImpact.Core.Mys;

/// <summary>
/// 把扫码链接画成 PNG。
/// <para>
/// 用 <see cref="PngByteQRCode"/> 而不是 <c>QRCode</c>：前者是纯托管实现，
/// 不依赖 System.Drawing，服务端渲染和单元测试里都能直接跑。
/// </para>
/// </summary>
public static class MysQrCode
{
    /// <summary>
    /// 渲染二维码。
    /// </summary>
    /// <param name="content">二维码内容，通常是扫码接口返回的登录链接。</param>
    /// <param name="pixelsPerModule">每个码点的像素边长；8 在手机上扫屏足够清楚。</param>
    public static byte[] Render(string content, int pixelsPerModule = 8)
    {
        using var generator = new QRCodeGenerator();
        using var data = generator.CreateQrCode(content, QRCodeGenerator.ECCLevel.M);
        return new PngByteQRCode(data).GetGraphic(pixelsPerModule);
    }
}

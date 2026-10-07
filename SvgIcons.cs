using System;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using SkiaSharp;
using Svg.Skia;

namespace ExplainingStones;

/// <summary>应用图标：加载 Assets/icons 下的 SVG，渲染成托盘图标。</summary>
public static class SvgIcons
{
    /// <summary>SVG 的设计尺寸（viewBox 边长）。</summary>
    private const float DesignSize = 24f;

    private static byte[]? _svg;

    /// <summary>渲染应用图标，调用方负责释放。</summary>
    public static Icon CreateAppIcon(int size)
    {
        using Bitmap bitmap = Render(size);
        IntPtr handle = bitmap.GetHicon();
        try
        {
            using Icon temp = Icon.FromHandle(handle);
            return (Icon)temp.Clone();
        }
        finally
        {
            DestroyIcon(handle);
        }
    }

    /// <summary>按目标边长渲染 SVG。</summary>
    private static Bitmap Render(int size)
    {
        using var svg = new SKSvg();
        using var stream = new MemoryStream(ReadSvg());
        svg.Load(stream);

        using var skia = new SKBitmap(new SKImageInfo(size, size, SKColorType.Rgba8888, SKAlphaType.Premul));
        using (var canvas = new SKCanvas(skia))
        {
            canvas.Clear(SKColors.Transparent);
            canvas.Scale(Math.Max(1, size) / DesignSize);
            using var paint = new SKPaint { IsAntialias = true };
            if (svg.Picture is not null)
            {
                canvas.DrawPicture(svg.Picture, paint);
            }
        }

        using SKData data = skia.Encode(SKEncodedImageFormat.Png, 100);
        using var png = new MemoryStream(data.ToArray());
        using var decoded = new Bitmap(png);
        return new Bitmap(decoded);   // 复制一份，脱离流
    }

    /// <summary>读取嵌入的 SVG 原始字节（带缓存）。</summary>
    private static byte[] ReadSvg()
    {
        if (_svg is not null)
        {
            return _svg;
        }

        const string suffix = ".icons.app.svg";
        foreach (string resource in typeof(SvgIcons).Assembly.GetManifestResourceNames())
        {
            if (!resource.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            using Stream stream = typeof(SvgIcons).Assembly.GetManifestResourceStream(resource)!;
            using var memory = new MemoryStream();
            stream.CopyTo(memory);
            _svg = memory.ToArray();
            return _svg;
        }

        throw new InvalidOperationException($"未找到图标资源：{suffix}");
    }

    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr handle);
}

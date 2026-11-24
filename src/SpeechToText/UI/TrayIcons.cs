using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Win32;
using Icon = System.Drawing.Icon;

namespace SpeechToText.UI;

/// <summary>
/// Idle: the OML family mark, using the owner-approved trace geometry from owmycontext
/// branding/assets/oml/favicon.svg — forest #07531b on a light taskbar, #4cc26a on a dark one
/// (the same two colors the SVG switches between). Not redrawn or recolored beyond that.
/// Recording / busy: a status disc in the danger-fill / warning tokens with the mic glyph.
/// </summary>
static class TrayIcons
{
    public static Icon Idle => TaskbarIsLight ? _idleOnLight.Value : _idleOnDark.Value;
    public static Icon Recording => _recording.Value;
    public static Icon Busy => _busy.Value;

    static readonly Lazy<Icon> _idleOnLight = new(() => Oml(Color.FromRgb(0x07, 0x53, 0x1b)));
    static readonly Lazy<Icon> _idleOnDark = new(() => Oml(Color.FromRgb(0x4c, 0xc2, 0x6a)));
    static readonly Lazy<Icon> _recording = new(() => Status(Color.FromRgb(0xdc, 0x26, 0x26), Colors.White));
    static readonly Lazy<Icon> _busy = new(() => Status(Color.FromRgb(0xf5, 0x9e, 0x0b), Color.FromRgb(0x0f, 0x11, 0x17)));

    const int Size = 32;

    static bool TaskbarIsLight
    {
        get
        {
            using var k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return k?.GetValue("SystemUsesLightTheme") is int v && v == 1;
        }
    }

    static Icon Oml(Color stroke)
    {
        // favicon.svg: viewBox 256, stroke-width 43, round caps.
        var geometry = Geometry.Parse("M128 30.5V225.5M30.5 128H225.5M128 128L60 196M128 128L196 196");
        var pen = new Pen(new SolidColorBrush(stroke), 43) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
        return ToIcon(dc =>
        {
            dc.PushTransform(new ScaleTransform(Size / 256.0, Size / 256.0));
            dc.DrawGeometry(null, pen, geometry);
            dc.Pop();
        });
    }

    static Icon Status(Color disc, Color glyph) => ToIcon(dc =>
    {
        dc.DrawEllipse(new SolidColorBrush(disc), null, new Point(16, 16), 16, 16);
        var text = new FormattedText("", CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
            new Typeface("Segoe Fluent Icons, Segoe MDL2 Assets"), 20, new SolidColorBrush(glyph), 1.0);
        dc.DrawText(text, new Point((Size - text.Width) / 2, (Size - text.Height) / 2));
    });

    static Icon ToIcon(Action<DrawingContext> draw)
    {
        var dv = new DrawingVisual();
        using (var dc = dv.RenderOpen()) draw(dc);
        var bmp = new RenderTargetBitmap(Size, Size, 96, 96, PixelFormats.Pbgra32);
        bmp.Render(dv);

        var png = new MemoryStream();
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(bmp));
        enc.Save(png);

        // An .ico may hold a PNG directly: 6-byte header + one 16-byte directory entry.
        var ico = new MemoryStream();
        using (var w = new BinaryWriter(ico, System.Text.Encoding.UTF8, leaveOpen: true))
        {
            w.Write((short)0); w.Write((short)1); w.Write((short)1);
            w.Write((byte)Size); w.Write((byte)Size); w.Write((byte)0); w.Write((byte)0);
            w.Write((short)1); w.Write((short)32); w.Write((int)png.Length); w.Write(22);
            w.Write(png.ToArray());
        }
        ico.Position = 0;
        return new Icon(ico);
    }
}

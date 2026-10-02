using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace WindrunnerLauncher.App.Controls;

/// <summary>
/// Draws a raster 9-slice: fixed corners, stretched edges, optional center fill.
/// Slice values are source pixels and also destination corner/edge thickness.
/// </summary>
public sealed class NineSliceImage : Control
{
    public static readonly StyledProperty<IImage?> SourceProperty =
        AvaloniaProperty.Register<NineSliceImage, IImage?>(nameof(Source));

    public static readonly StyledProperty<Thickness> SliceProperty =
        AvaloniaProperty.Register<NineSliceImage, Thickness>(nameof(Slice), new Thickness(64));

    public static readonly StyledProperty<bool> FillCenterProperty =
        AvaloniaProperty.Register<NineSliceImage, bool>(nameof(FillCenter), true);

    static NineSliceImage()
    {
        AffectsRender<NineSliceImage>(SourceProperty, SliceProperty, FillCenterProperty);
        ClipToBoundsProperty.OverrideDefaultValue<NineSliceImage>(false);
        IsHitTestVisibleProperty.OverrideDefaultValue<NineSliceImage>(false);
    }

    public IImage? Source
    {
        get => GetValue(SourceProperty);
        set => SetValue(SourceProperty, value);
    }

    public Thickness Slice
    {
        get => GetValue(SliceProperty);
        set => SetValue(SliceProperty, value);
    }

    public bool FillCenter
    {
        get => GetValue(FillCenterProperty);
        set => SetValue(FillCenterProperty, value);
    }

    public override void Render(DrawingContext context)
    {
        var src = Source;
        if (src is null)
            return;

        var dest = Bounds.Size;
        if (dest.Width <= 0 || dest.Height <= 0)
            return;

        var sw = src.Size.Width;
        var sh = src.Size.Height;
        if (sw <= 0 || sh <= 0)
            return;

        var sl = Math.Max(1, Math.Min(Slice.Left, sw / 2));
        var st = Math.Max(1, Math.Min(Slice.Top, sh / 2));
        var sr = Math.Max(1, Math.Min(Slice.Right, sw / 2));
        var sb = Math.Max(1, Math.Min(Slice.Bottom, sh / 2));

        var scale = 1.0;
        if (sl + sr > dest.Width || st + sb > dest.Height)
            scale = Math.Min(dest.Width / (sl + sr), dest.Height / (st + sb));

        var dl = sl * scale;
        var dt = st * scale;
        var dr = sr * scale;
        var db = sb * scale;
        var dw = dest.Width;
        var dh = dest.Height;

        void Patch(Rect source, Rect target)
        {
            if (source.Width < 0.5 || source.Height < 0.5 || target.Width < 0.5 || target.Height < 0.5)
                return;
            context.DrawImage(src, source, target);
        }

        Patch(new Rect(0, 0, sl, st), new Rect(0, 0, dl, dt));
        Patch(new Rect(sw - sr, 0, sr, st), new Rect(dw - dr, 0, dr, dt));
        Patch(new Rect(0, sh - sb, sl, sb), new Rect(0, dh - db, dl, db));
        Patch(new Rect(sw - sr, sh - sb, sr, sb), new Rect(dw - dr, dh - db, dr, db));

        Patch(new Rect(sl, 0, sw - sl - sr, st), new Rect(dl, 0, dw - dl - dr, dt));
        Patch(new Rect(sl, sh - sb, sw - sl - sr, sb), new Rect(dl, dh - db, dw - dl - dr, db));
        Patch(new Rect(0, st, sl, sh - st - sb), new Rect(0, dt, dl, dh - dt - db));
        Patch(new Rect(sw - sr, st, sr, sh - st - sb), new Rect(dw - dr, dt, dr, dh - dt - db));

        if (FillCenter)
            Patch(new Rect(sl, st, sw - sl - sr, sh - st - sb),
                new Rect(dl, dt, dw - dl - dr, dh - dt - db));
    }
}

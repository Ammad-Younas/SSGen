namespace SSGen.Scene.Editing;

/// <summary>
/// "Apply this style to all images": makes another image of the same size look like the edited one while keeping
/// its own content. Layers are matched by id (template roles such as "headline" or "device"):
/// <list type="bullet">
/// <item>matched layers take the source's position, size, colours, fonts, frame and effects, but keep their own
/// text and picture;</item>
/// <item>layers the user added to the source (badges, shapes, stickers) are copied;</item>
/// <item>layers deleted from the source are removed;</item>
/// <item>the background takes the source's colours, keeping each image's own panorama offset; in panorama sets
/// the template's shapes stay shifted by the image's place in the strip.</item>
/// </list>
/// </summary>
public static class StyleTransfer
{
    public static SceneDocument Apply(SceneDocument source, SceneDocument target)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(target);
        var result = target.Clone();
        result.Background = Background(source.Background, target.Background);

        // Panorama: image n of the set sees the strip shifted left by n widths.
        var stripShift = (source.Background, target.Background) is (LinearGradientFill { Span: > 1 } s, LinearGradientFill { Span: > 1 } t)
            ? (t.Offset - s.Offset) * target.Width
            : 0;

        var own = target.Layers.ToDictionary(l => l.Id);
        result.Layers = [];
        foreach (var layer in source.Layers)
        {
            var copy = LayerJson.Read(LayerJson.Write(layer));
            if (own.TryGetValue(layer.Id, out var mine))
            {
                KeepContent(copy, mine);
                if (copy is ShapeLayer)
                {
                    copy.X -= stripShift;
                }
            }

            result.Layers.Add(copy);
        }

        return result;
    }

    private static void KeepContent(Layer copy, Layer mine)
    {
        switch (copy, mine)
        {
            case (TextLayer c, TextLayer m):
                c.Text = m.Text;
                break;
            case (ImageLayer c, ImageLayer m):
                c.Source = m.Source;
                break;
            case (DeviceLayer c, DeviceLayer m):
                c.Source = m.Source;
                break;
            case (ShapeLayer c, ShapeLayer m):
                c.Fill = Background(c.Fill, m.Fill);
                break;
        }
    }

    /// <summary>The source's fill, but a panorama gradient keeps the target's own offset and span.</summary>
    private static Fill Background(Fill source, Fill target) => (source, target) switch
    {
        (LinearGradientFill s, LinearGradientFill t) => s with { Offset = t.Offset, Span = t.Span },
        _ => source,
    };
}

## OpenTypeOnDemand

Some sort of text layout engine for MonoGame (DirectX), using FreeType.

Draws text and returns list of textures.

**Work in progress. Do not use this in production and no animal gets harmed.**

### Usage

```csharp
foreach (var glyph in OpenTypeOnDemand.Render(...))
{
    SpriteBatch.Draw(glyph.texture, glyph.position, glyph.color);
}
```

Glyph textures are automatically cached in RAM(?) so calling this on every `Draw()` cycle would be hopefully not too painful. But keep in mind the idea of caching everything may cause serious issues, especially when dealing with a large variety of characters and/or styles.

### Planned features

+ [ ] basic horizontal ltr latin with kerning

  partially implemented

+ [ ] wrapping

  partially implemented

+ [x] ability to specify color, size, line height & font face of runs respectively
+ [x] font fallback

#### Low priority features

No promises.

+ [ ] ligature
+ [ ] various opentype features
+ [ ] ruby
+ [ ] vertical rl
+ [ ] horizontal rtl
+ [ ] bidi

#### Not planned features

+ automatic discovery of other faces in a font family

  specify the corresponding variant as typeface for this purpose instead

+ loading fallback font from os

  pay me $8848 and I would definitely reconsider

+ crossplatform

  feel free to fork and port it

### Why not use Pango?

> Not Found
>
> The requested URL /download/win32.php was not found on this server.

> Not Found
>
> The requested URL /download/win64.php was not found on this server.

*— https://www.pango.org/Download*

### Why not use SpriteFont?

Try build a SpriteFont for any single variant of Noto Sans CJK / Source Han Sans within an hour.

### Why not use BMFont (powered by MonoGame.Extended)?

This builds, but not like it has font fallback and stuff.

### License

LGPLv3
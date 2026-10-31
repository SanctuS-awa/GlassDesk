using System.Numerics;
using System.Globalization;
using GlassDesk.Core;
using GlassDesk.NativeBridge;
using Windows.UI.Composition;

namespace GlassDesk.App;

public sealed partial class WidgetScene
{
    // Keep the full soft edge; same-polarity Peek clock and metadata supports are
    // combined before composition so their overlapping alpha is not stacked.
    const float ReadingSupportFeather = 20;
    // A content-group reading role, not another glass card or a background probe.
    // Only app-owned vector colors enter this surface; no captured pixels/effect graph.
    readonly Dictionary<WidgetForm, ReadingSupportVisual> _readingSupport = new();
    readonly Dictionary<WidgetForm, (SurfaceOp[] Operations, TypographyIdentity Type, FloatRect First, FloatRect Second)> _readingPlatforms = new();
    ReadingSupportVisual? _capsuleClockReading, _peekClockReading;
    ReadingSupportVisual? _combinedPeekReading;
    ReadingSupportVisual? _disconnectedPeekReading;
    SurfaceOp[]? _disconnectedPeekText, _disconnectedMeasuredText;
    FloatRect[] _disconnectedLineBounds = [];
    bool[] _disconnectedInkGeometry = [];
    SurfaceOp[]? _combinedClockOperationsKey;
    uint _combinedClockDpiKey;
    FloatRect[] _combinedClockPlatforms = [];
    FloatRect[] _combinedPeekReadingBounds = [];
    bool _disconnectedPeekLargeClock;
    (string Primary, string Secondary, string Font, bool Time, TypographyIdentity Type)? _clockReadingKey;
    float _capsuleClockReadingWidth, _peekClockReadingWidth;
    bool _readingTransitionWasActive;
    bool ReadingSupportMotionActive => !_host.IsSystemCompositorCandidate && OwnedGlassEnabled
        && _metadata.Values.Any(layer => layer.IsTransitioning && layer.Visual.Opacity > .001f);
    bool TickReadingSupportMotion()
    {
        bool active = ReadingSupportMotionActive;
        bool changed = active != _readingTransitionWasActive;
        _readingTransitionWasActive = active;
        return changed;
    }

    void UpdateReadingSupport()
    {
        // Desktop glass now uses the user's explicit foreground ink. Do not
        // paint, measure or regenerate local white/dark readability clouds.
        // Retire any retained fields as well, including a form mid-crossfade.
        if (_host.IsSystemCompositorCandidate)
        {
            foreach (var support in _readingSupport.Values) support.Visual.IsVisible = false;
            if (_capsuleClockReading is not null) _capsuleClockReading.Visual.IsVisible = false;
            if (_peekClockReading is not null) _peekClockReading.Visual.IsVisible = false;
            if (_disconnectedPeekReading is not null) _disconnectedPeekReading.Visual.IsVisible = false;
            HideCombinedPeekReading();
            return;
        }
        foreach (var form in new[] { WidgetForm.Capsule, WidgetForm.Peek, WidgetForm.Details })
        {
            var metadata = _metadata[form].Visual;
            bool visible = OwnedGlassEnabled && metadata.Opacity > .001f && metadata.Size.X > 0;
            if (!_readingSupport.TryGetValue(form, out var support))
            {
                if (!visible) continue;
                support = new(_host);
                // The scroll viewport starts at the title. Putting its feather inside
                // that clip cuts a hard white stripe across the artwork's lower edge.
                _bodyRoot.Children.InsertBelow(support.Visual,
                    form == WidgetForm.Capsule ? _capsuleContent : form == WidgetForm.Peek ? _peekContent : _detailsViewport);
                _readingSupport.Add(form, support);
            }
            float width = metadata.Size.X;
            (FloatRect First, FloatRect Second)? readingLines = null;
            if (visible && _metadataOperations.TryGetValue(form, out var operations))
            {
                // Each form keeps the operations that produced its visible surface. A
                // different form may already show a new track while this one is outgoing.
                var identity = _metadataKeys.TryGetValue(form, out var rendered) ? rendered.Typography : CurrentTypography;
                if (!_readingPlatforms.TryGetValue(form, out var platforms)
                    || platforms.Type != identity
                    || !platforms.Operations.AsSpan().SequenceEqual(operations))
                {
                    FloatRect first = default, second = default;
                    bool hasMetadataText = false;
                    var canvasSize = new Vector2(metadata.Size.X, metadata.Size.Y);
                    foreach (var operation in operations)
                    {
                        bool title = operation.TextRole is TextRole.MediaCapsuleTitle or TextRole.MediaPeekTitle or TextRole.MediaDetailsTitle;
                        bool artist = operation.TextRole is TextRole.MediaCapsuleArtist or TextRole.MediaPeekArtist or TextRole.MediaDetailsArtist;
                        if (!title && !artist) continue;
                        hasMetadataText = true;
                        var platform = MetadataReadingPlatform(operation, canvasSize);
                        if (title) first = platform;
                        else second = platform;
                    }
                    platforms = (operations, identity, first, second);
                    _readingPlatforms[form] = platforms;
                    if (!hasMetadataText) readingLines = null;
                }
                if (readingLines is null && (platforms.First != default || platforms.Second != default))
                    readingLines = (platforms.First, platforms.Second);
            }
            bool transitioning = _metadata[form].IsTransitioning;
            float weight = form == WidgetForm.Capsule ? _contentWeights.X : form == WidgetForm.Peek ? _contentWeights.Y : _contentWeights.Z;
            if (transitioning) { width = metadata.Size.X + 36; readingLines = null; } // Protect both +/-18 DIP outgoing/incoming tracks.
            Vector3 parent = form == WidgetForm.Capsule ? _capsuleContent.Offset : form == WidgetForm.Peek ? _peekContent.Offset : _detailsViewport.Offset + _detailsBody.Offset;
            Vector3 position = parent + metadata.Offset;
            if (transitioning) position.X -= 18;
            // This is a geometry gate, not a read of any composed backdrop.
            if (form == WidgetForm.Details)
                visible &= position.Y + metadata.Size.Y > _detailsViewport.Offset.Y
                    && position.Y < _detailsViewport.Offset.Y + _detailsViewport.Size.Y;
            var material = ReadingMaterialPolicy.Resolve(_lightText, _preferences.TintOpacity);
            support.Update(new(width, metadata.Size.Y), position, metadata.Opacity * weight, visible, material, readingLines);
            support.SetViewport(form == WidgetForm.Details
                ? new FloatRect(_detailsViewport.Offset.X, _detailsViewport.Offset.Y - ReadingSupportFeather,
                    _detailsViewport.Size.X, _detailsViewport.Size.Y + ReadingSupportFeather) : null);
        }
        UpdateClockReadingSupport();
        UpdateDisconnectedPeekReading();
        UpdateCombinedPeekReading();
    }

    FloatRect MetadataReadingPlatform(SurfaceOp operation, Vector2 canvasSize)
    {
        if (TryInkReadingPlatform(operation, canvasSize, out var inkPlatform)) return inkPlatform;
        var measured = _host.MeasureText(operation with { X = 0, Y = 0, Width = 32768, Center = false });
        float width = Math.Clamp(MathF.Ceiling(measured.Advance + 4), 1, operation.Width);
        float x = operation.X + (operation.Center ? (operation.Width - width) / 2 : 0);
        return new(x, operation.Y, width, operation.Height);
    }

    void UpdateDisconnectedPeekReading()
    {
        // The retained operations belong to the actual Peek surface, including
        // its outgoing fade. A global connection/form switch must not hide them early.
        bool visible = OwnedGlassEnabled && _contentWeights.Y > .001f && _disconnectedPeekText is { Length: > 0 };
        if (!visible) { if (_disconnectedPeekReading is not null) _disconnectedPeekReading.Visual.IsVisible = false; return; }
        var operations = _disconnectedPeekText!;
        if (operations.Length > 4) throw new InvalidOperationException("Disconnected reading support exceeds its bounded line count.");
        if (_disconnectedMeasuredText is null || !operations.AsSpan().SequenceEqual(_disconnectedMeasuredText))
        {
            _disconnectedInkGeometry = new bool[operations.Length];
            _disconnectedLineBounds = operations.Select((op, index) => {
                if (TryInkReadingPlatform(op, _peekContent.Size, out var inkPlatform))
                {
                    _disconnectedInkGeometry[index] = true;
                    return inkPlatform;
                }
                var measured = _host.MeasureText(op with { X = 0, Y = 0, Width = 32768, Center = false });
                float width = Math.Clamp(MathF.Ceiling(measured.Advance + 4), 1, op.Width);
                return new FloatRect(op.X + (op.Width - width) / 2, op.Y, width, op.Height);
            }).ToArray();
            _disconnectedMeasuredText = operations;
        }
        Span<ReadingSupportMaskLine> lines = stackalloc ReadingSupportMaskLine[operations.Length];
        for (int i = 0; i < operations.Length; i++)
        {
            var op = operations[i];
            bool lightInk = op.Color == TypographyCatalog.Ink(InkRole.Primary, darkMaterial: true);
            if (op.Color != TypographyCatalog.Ink(InkRole.Primary, darkMaterial: lightInk))
                throw new InvalidOperationException("Disconnected glass text requires the resolved primary ink family.");
            var material = _disconnectedPeekLargeClock && op.TextRole == TextRole.ClockPeekHero
                ? ReadingMaterialPolicy.ResolveLargeClock(lightInk, _preferences.TintOpacity)
                : ReadingMaterialPolicy.Resolve(lightInk, _preferences.TintOpacity);
            lines[i] = new(_disconnectedLineBounds[i], material);
        }
        if (_disconnectedPeekReading is null)
        {
            _disconnectedPeekReading = new(_host);
            _bodyRoot.Children.InsertBelow(_disconnectedPeekReading.Visual, _peekContent);
        }
        var canvas = new ReadingSupportMaskCanvas(_peekContent.Size.X, _peekContent.Size.Y, _host.Context.Dpi / 96f, ReadingSupportFeather);
        _disconnectedPeekReading.UpdateRows(canvas, lines, _peekContent.Offset, _contentWeights.Y);
    }

    void UpdateCombinedPeekReading()
    {
        var metadata = _metadata[WidgetForm.Peek];
        float weight = _contentWeights.Y;
        bool hasMetadataOperations = _metadataOperations.TryGetValue(WidgetForm.Peek, out var metadataOperations);
        bool eligible = OwnedGlassEnabled && _media.Connected && weight > .001f
            && metadata.Visual.Opacity == 1
            && _ownedClock is not null && _ownedClock.Opacity == weight
            && _ownedClockOperations is { Length: > 0 }
            && hasMetadataOperations
            && metadata.Visual.Size.X > 0 && metadata.Visual.Size.Y > 0;
        if (!eligible)
        {
            HideCombinedPeekReading();
            return;
        }

        var metadataMaterial = ReadingMaterialPolicy.Resolve(_lightText, _preferences.TintOpacity);
        var clockMaterial = ReadingMaterialPolicy.Resolve(_clockPlateLightText, _preferences.TintOpacity);
        if (metadataMaterial.ColorArgb != clockMaterial.ColorArgb)
        {
            HideCombinedPeekReading();
            return;
        }

        Span<ReadingSupportMaskLine> lines = stackalloc ReadingSupportMaskLine[4];
        int count = 0;
        if (metadata.IsTransitioning)
        {
            // Keep the existing complete metadata slot and +/-18 DIP protection
            // until the actual incoming/outgoing surface handoff has settled.
            lines[count++] = new(new(metadata.Visual.Offset.X - 18, metadata.Visual.Offset.Y,
                metadata.Visual.Size.X + 36, metadata.Visual.Size.Y), metadataMaterial);
        }
        else if (_readingPlatforms.TryGetValue(WidgetForm.Peek, out var platforms)
            && platforms.Operations.AsSpan().SequenceEqual(metadataOperations!))
        {
            AddPeekMetadataPlatform(platforms.First, metadata.Visual.Offset, metadataMaterial, lines, ref count);
            AddPeekMetadataPlatform(platforms.Second, metadata.Visual.Offset, metadataMaterial, lines, ref count);
        }
        else
        {
            HideCombinedPeekReading();
            return;
        }

        var clockPlatforms = GetCombinedClockPlatforms(_ownedClockOperations!);
        if (clockPlatforms.Length == 0 || count + clockPlatforms.Length > lines.Length)
        {
            HideCombinedPeekReading();
            return;
        }
        Vector2 parallax = OwnedHeaderParallax(false);
        bool headerMoves = MathF.Abs(parallax.X) > .001f || MathF.Abs(parallax.Y) > .001f;
        float clockX = PeekClockX; // Base position; parallax is covered by the envelope below.
        foreach (var bounds in clockPlatforms)
        {
            var envelope = headerMoves
                ? new FloatRect(bounds.X + clockX, bounds.Y, bounds.Width + 10, bounds.Height + 7)
                : bounds with { X = bounds.X + clockX };
            if (envelope != default) lines[count++] = new(envelope, clockMaterial);
        }
        if (count == 0)
        {
            HideCombinedPeekReading();
            return;
        }

        if (_combinedPeekReading is null)
        {
            _combinedPeekReading = new(_host);
            _bodyRoot.Children.InsertBelow(_combinedPeekReading.Visual, _peekContent);
        }
        var canvas = new ReadingSupportMaskCanvas(320, 176, _host.Context.Dpi / 96f, ReadingSupportFeather);
        _combinedPeekReading.UpdateRows(canvas, lines[..count], _peekContent.Offset, weight);
        bool boundsChanged = _combinedPeekReadingBounds.Length != count;
        for (int i = 0; !boundsChanged && i < count; i++)
            boundsChanged = _combinedPeekReadingBounds[i] != lines[i].Bounds;
        if (boundsChanged)
        {
            _combinedPeekReadingBounds = new FloatRect[count];
            for (int i = 0; i < count; i++) _combinedPeekReadingBounds[i] = lines[i].Bounds;
        }
        if (_readingSupport.TryGetValue(WidgetForm.Peek, out var separateMetadata))
            separateMetadata.Visual.IsVisible = false;
        if (_peekClockReading is not null) _peekClockReading.Visual.IsVisible = false;
    }

    static void AddPeekMetadataPlatform(FloatRect platform, Vector3 offset,
        ReadingMaterialSupport material, Span<ReadingSupportMaskLine> lines, ref int count)
    {
        if (platform == default || count >= lines.Length) return;
        lines[count++] = new(new(platform.X + offset.X, platform.Y + offset.Y, platform.Width, platform.Height), material);
    }

    FloatRect[] GetCombinedClockPlatforms(SurfaceOp[] operations)
    {
        uint dpi = _host.Context.Dpi;
        if (ReferenceEquals(_combinedClockOperationsKey, operations) && _combinedClockDpiKey == dpi)
            return _combinedClockPlatforms;

        var measured = new List<FloatRect>(operations.Length);
        foreach (var operation in operations)
        {
            if (string.IsNullOrEmpty(operation.Text)) continue;
            var platform = MetadataReadingPlatform(operation, _ownedClock!.Size);
            if (platform != default) measured.Add(platform);
        }
        _combinedClockOperationsKey = operations;
        _combinedClockDpiKey = dpi;
        _combinedClockPlatforms = measured.ToArray();
        return _combinedClockPlatforms;
    }

    void HideCombinedPeekReading()
    {
        if (_combinedPeekReading is not null) _combinedPeekReading.Visual.IsVisible = false;
        _combinedPeekReadingBounds = [];
    }

    bool TryInkReadingPlatform(SurfaceOp operation, Vector2 canvasSize, out FloatRect platform)
    {
        platform = default;
        // Color/combining runs have not been certified by the geometry probe.
        // Keep their conservative line-slot support rather than guessing pixels.
        if (operation.Text is null || operation.Text.Any(c => char.IsSurrogate(c) || char.GetUnicodeCategory(c) is
            UnicodeCategory.OtherSymbol or UnicodeCategory.NonSpacingMark or UnicodeCategory.EnclosingMark or UnicodeCategory.Format)) return false;
        var measured = _host.MeasureTextInkBounds(operation);
        if (measured is not { } ink) return false; // Optional data ABI on older native builds.
        if (ink.IsEmpty) return true;
        float scale = _host.Context.Dpi / 96f;
        // DWrite extents are geometry, not final hinted pixels. Keep at least
        // two physical pixels of support around that geometry, in DIPs once.
        float padding = Math.Max(1.5f, 2 / scale);
        float left = Math.Clamp(operation.X + ink.Left - padding, 0, canvasSize.X);
        float top = Math.Clamp(operation.Y + ink.Top - padding, 0, canvasSize.Y);
        float right = Math.Clamp(operation.X + ink.Right + padding, 0, canvasSize.X);
        float bottom = Math.Clamp(operation.Y + ink.Bottom + padding, 0, canvasSize.Y);
        if (right > left && bottom > top) platform = new(left, top, right - left, bottom - top);
        return true;
    }

    void UpdateClockReadingSupport()
    {
        bool hasText = ClockPrimary.Length > 0 || ClockSecondary.Length > 0;
        bool capsule = OwnedGlassEnabled && hasText && !MusicFor(WidgetForm.Capsule) && _contentWeights.X > .001f;
        bool peek = OwnedGlassEnabled && hasText && _media.Connected && _ownedClock is { Opacity: > .001f };
        if (!capsule && !peek)
        {
            if (_capsuleClockReading is not null) _capsuleClockReading.Visual.IsVisible = false;
            if (_peekClockReading is not null) _peekClockReading.Visual.IsVisible = false;
            return;
        }
        EnsureClockReadingMeasurements();
        bool secondary = ClockSecondary.Length > 0;
        if (_capsuleClockReading is null && capsule)
        {
            _capsuleClockReading = new(_host);
            _bodyRoot.Children.InsertBelow(_capsuleClockReading.Visual, _capsuleContent);
        }
        if (_capsuleClockReading is not null)
        {
            float left = CompanionVisible ? 56 : 16, available = LayoutSize(WidgetForm.Capsule).X - left - 16;
            float width = Math.Clamp(_capsuleClockReadingWidth, 1, available);
            _capsuleClockReading.Update(new(width, secondary ? 43 : 32),
                _capsuleContent.Offset + new Vector3(left + (available - width) / 2, secondary ? 5 : 10, 0),
                _contentWeights.X, capsule, _preferences.ClockShowTime && !secondary
                    ? ReadingMaterialPolicy.ResolveLargeClock(_lightText, _preferences.TintOpacity)
                    : ReadingMaterialPolicy.Resolve(_lightText, _preferences.TintOpacity));
        }
        if (_peekClockReading is null && peek)
        {
            _peekClockReading = new(_host);
            _bodyRoot.Children.InsertBelow(_peekClockReading.Visual, _ownedClock!);
        }
        if (_peekClockReading is not null && _ownedClock is not null)
        {
            float width = Math.Clamp(_peekClockReadingWidth, 1, _ownedClock.Size.X);
            _peekClockReading.Update(new(width, secondary ? 38 : 26),
                _ownedClock.Offset + new Vector3((_ownedClock.Size.X - width) / 2, secondary ? 10 : 15, 0),
                _ownedClock.Opacity, peek, ReadingMaterialPolicy.Resolve(_clockPlateLightText, _preferences.TintOpacity));
        }
    }

    void EnsureClockReadingMeasurements()
    {
        var typography = CurrentTypography;
        var key = (ClockPrimary, ClockSecondary, _preferences.ClockFontId, _preferences.ClockShowTime, typography);
        if (_clockReadingKey == key) return;

        bool hasText = ClockPrimary.Length > 0 || ClockSecondary.Length > 0;
        if (!hasText)
        {
            _capsuleClockReadingWidth = _peekClockReadingWidth = 0;
            _clockReadingKey = key;
            return;
        }

        float Advance(string value, TextRole role)
        {
            var token = TypographyCatalog.Get(role);
            var operation = SurfaceOp.Typography(value, role, 0, 0, 32768, token.LineHeightDip,
                TypographyCatalog.Ink(InkRole.Primary),
                numericFlags: role == TextRole.ClockLegacyAux ? null : _preferences.ClockShowTime ? TextFlags.TabularNumbers : TextFlags.TabularDigitsOnly,
                locale: typography.Locale);
            if (_preferences.ClockShowTime && ClockFontCatalog.IsClockRole(role))
                operation = operation with { TextFlags = operation.TextFlags | ClockFontCatalog.Get(_preferences.ClockFontId).Flag };
            return _host.MeasureText(operation).Advance;
        }

        float auxiliary = ClockSecondary.Length == 0 ? 0 : Advance(ClockSecondary, TextRole.ClockLegacyAux);
        float capsulePrimary = ClockPrimary.Length == 0 ? 0 : Advance(ClockPrimary,
            _preferences.ClockShowTime ? TextRole.ClockCapsule : TextRole.Label);
        float peekPrimary = ClockPrimary.Length == 0 ? 0 : Advance(ClockPrimary, TextRole.ClockWhiteplate);
        _capsuleClockReadingWidth = MathF.Ceiling(Math.Max(auxiliary, capsulePrimary) + 4);
        _peekClockReadingWidth = MathF.Ceiling(Math.Max(auxiliary, peekPrimary) + 4);
        _clockReadingKey = key;
    }

    void DisposeReadingSupport()
    {
        foreach (var support in _readingSupport.Values) support.Dispose();
        _readingSupport.Clear();
        _readingPlatforms.Clear();
        _capsuleClockReading?.Dispose(); _peekClockReading?.Dispose();
        _capsuleClockReading = _peekClockReading = null;
        _combinedPeekReading?.Dispose(); _combinedPeekReading = null;
        _combinedPeekReadingBounds = []; _combinedClockOperationsKey = null; _combinedClockPlatforms = [];
        _disconnectedPeekReading?.Dispose(); _disconnectedPeekReading = null;
        _disconnectedPeekText = _disconnectedMeasuredText = null; _disconnectedLineBounds = []; _disconnectedInkGeometry = [];
    }

    void InvalidateReadingSupport()
    {
        foreach (var support in _readingSupport.Values) support.Invalidate();
        _readingPlatforms.Clear();
        _clockReadingKey = null;
        _capsuleClockReading?.Invalidate(); _peekClockReading?.Invalidate();
        _combinedPeekReading?.Invalidate(); _combinedPeekReading?.Visual.IsVisible = false;
        _combinedPeekReadingBounds = []; _combinedClockOperationsKey = null; _combinedClockPlatforms = [];
        _disconnectedPeekReading?.Invalidate(); _disconnectedMeasuredText = null;
    }

    sealed class ReadingSupportVisual : IDisposable
    {
        const float Feather = ReadingSupportFeather;
        readonly NativeWidgetHost _host;
        readonly CompositionSurfaceBrush _brush;
        CompositionDrawingSurface? _surface;
        InsetClip? _viewportClip;
        (Vector2 Size, ReadingMaterialSupport Material, ReadingSupportMaskLayout? Mask)? _key;
        ReadingSupportMaskCanvas? _rowsCanvas;
        ReadingSupportMaskLine[] _rows = [];
        public SpriteVisual Visual { get; }

        public ReadingSupportVisual(NativeWidgetHost host)
        {
            _host = host;
            Visual = host.Compositor.CreateSpriteVisual();
            _brush = host.Compositor.CreateSurfaceBrush();
            _brush.Stretch = CompositionStretch.Fill;
            Visual.Brush = _brush;
        }

        public void Update(Vector2 textSize, Vector3 position, float opacity, bool visible, ReadingMaterialSupport material,
            (FloatRect First, FloatRect Second)? readingLines = null)
        {
            Visual.IsVisible = visible && material.Opacity8Bit > 0;
            if (!Visual.IsVisible) return;
            Vector2 size = textSize + new Vector2(2 * Feather);
            Visual.Size = size;
            Visual.Offset = position - new Vector3(Feather, Feather, 0);
            // Inherits page opacity/scroll; remains a single field during track crossfade.
            Visual.Opacity = opacity;
            ReadingSupportMaskLayout? mask = readingLines is { } lines
                ? new(size.X, size.Y, _host.Context.Dpi / 96f,
                    lines.First with { X = lines.First.X + Feather, Y = lines.First.Y + Feather },
                    lines.Second with { X = lines.Second.X + Feather, Y = lines.Second.Y + Feather }, Feather) : null;
            var key = (size, material, mask);
            if (_key == key) return;
            _rowsCanvas = null; _rows = [];
            var operations = new List<SurfaceOp>();
            if (mask is { } shaped)
            {
                // An app-generated two-line alpha union, not a background image.
                // No additive overlap, border, sampled texture, or new effect graph.
                operations.Add(new SurfaceOp { Kind = SurfaceOpKind.Image, Width = size.X, Height = size.Y,
                    Image = ReadingSupportMask.CreatePng(shaped, material) });
                ReplaceSurface(ref _surface, _brush, _host.RenderSurface(size.X, size.Y, operations));
                _key = key;
                return;
            }
            float accumulated = 0;
            const int steps = 64;
            for (int i = 1; i <= steps; i++)
            {
                float t = i / (float)steps;
                float desired = material.Opacity * t * t * (3 - 2 * t);
                int alpha = Math.Clamp((int)Math.Round(255 * (desired - accumulated) / (1 - accumulated)), 0, 255);
                if (alpha == 0) continue;
                float inset = Feather * t;
                operations.Add(new SurfaceOp {
                    Kind = SurfaceOpKind.RoundedRectangle,
                    X = inset, Y = inset, Width = size.X - 2 * inset, Height = size.Y - 2 * inset,
                    Radius = Feather - inset + 4,
                    Color = ((uint)alpha << 24) | (material.ColorArgb & 0x00ffffffu)
                });
                accumulated += (1 - accumulated) * alpha / 255f;
            }
            ReplaceSurface(ref _surface, _brush, _host.RenderSurface(size.X, size.Y, operations));
            _key = key;
        }

        public void UpdateRows(ReadingSupportMaskCanvas canvas, ReadOnlySpan<ReadingSupportMaskLine> lines,
            Vector3 position, float opacity)
        {
            bool anySupport=false;
            foreach(var line in lines)anySupport|=line.Material.Opacity8Bit>0&&line.Bounds.Width>0&&line.Bounds.Height>0;
            if(!anySupport){Visual.IsVisible=false;return;}
            Visual.IsVisible = true; Visual.Size = new(canvas.WidthDip, canvas.HeightDip);
            Visual.Offset = position; Visual.Opacity = opacity; Visual.Clip = null;
            if (_rowsCanvas == canvas && lines.SequenceEqual(_rows)) return;
            byte[] ownMask = ReadingSupportMask.CreateLinesPng(canvas, lines);
            ReplaceSurface(ref _surface, _brush, _host.RenderSurface(canvas.WidthDip, canvas.HeightDip,
                [new SurfaceOp { Kind = SurfaceOpKind.Image, Width = canvas.WidthDip, Height = canvas.HeightDip, Image = ownMask }]));
            _rowsCanvas = canvas; _rows = lines.ToArray(); _key = null;
        }

        public void Dispose()
        {
            Visual.Clip = null;
            Visual.Brush = null;
            _brush.Surface = null;
            _surface?.Dispose();
            Visual.Dispose();
            _brush.Dispose();
            _viewportClip?.Dispose();
        }

        public void SetViewport(FloatRect? viewport)
        {
            if (viewport is not { } bounds) { Visual.Clip = null; return; }
            _viewportClip ??= _host.Compositor.CreateInsetClip();
            _viewportClip.LeftInset = Math.Max(0, bounds.X - Visual.Offset.X);
            _viewportClip.TopInset = Math.Max(0, bounds.Y - Visual.Offset.Y);
            _viewportClip.RightInset = Math.Max(0, Visual.Offset.X + Visual.Size.X - bounds.Right);
            _viewportClip.BottomInset = Math.Max(0, Visual.Offset.Y + Visual.Size.Y - bounds.Bottom);
            Visual.Clip = _viewportClip;
        }

        public void Invalidate() { _key = null; _rowsCanvas = null; _rows = []; }
    }
}

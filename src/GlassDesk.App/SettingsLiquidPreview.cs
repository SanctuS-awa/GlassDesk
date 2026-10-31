using System.Diagnostics;
using System.Numerics;
using GlassDesk.Core;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Foundation;
using VirtualKey = Windows.System.VirtualKey;
using Windows.UI;


namespace GlassDesk.App;

/// <summary>Independent local optical demo. Its backgrounds and material controls do not mirror the desktop renderer.</summary>
internal sealed class SettingsLiquidPreview : StackPanel, IDisposable
{
    readonly LiquidTransitionPrototype _model = new();
    readonly Stopwatch _clock = Stopwatch.StartNew();
    readonly DispatcherQueueTimer _timer;
    readonly Func<bool> _reduced;
    readonly Func<Vector2[], Windows.Graphics.IGeometrySource2D?> _createGeometry;
    Microsoft.UI.Composition.CompositionPathGeometry? _contentGeometry;
    readonly Canvas _stage = new() { Width = 640, Height = 252 };
    readonly Canvas _shell = new() { Width = 352, Height = 208 };
    readonly Canvas _contents = new() { Width = 352, Height = 208 };
    readonly SettingsGlassSurface _surface = new() { Width=640, Height=252 };
    readonly SettingsGlassSlider _progress, _tension, _bounce, _response, _lens, _blur, _tint, _sceneShift;
    readonly SettingsGlassSwitch _happy;
    readonly TextBlock _scope=Text("演示独立背景中的轮廓与布局；实际材质以桌面组件为准。",11);
    readonly List<SettingsGlassSlider> _sliders=[];
    readonly float[] _buttonActivity=new float[3], _buttonTarget=new float[3];
    readonly Func<nint> _owner;
    readonly List<ToggleButton> _backgroundButtons=[];
    readonly Microsoft.UI.Xaml.Controls.Button _chooseImage=new() { Content="选择图片",MinHeight=32 };
    readonly Microsoft.UI.Xaml.Controls.Button _magnifier=new() { Width=96,Height=96,MinWidth=0,MinHeight=0,Padding=new(0),CornerRadius=new(0),Background=Brush(0),BorderThickness=new(0),IsTabStop=true,UseSystemFocusVisuals=true };
    readonly TextBlock _magnifierLabel=Text("背景透镜",9);
    readonly ToggleButton _opticsOnly=new() { Content="仅看透镜",MinHeight=32,CornerRadius=new(8) };
    readonly ToggleButton _lighting=new() { Content="光照",IsChecked=true,MinHeight=32,CornerRadius=new(8) };
    readonly Microsoft.UI.Xaml.Shapes.Line _magnifierHandle=new() { X1=78,Y1=78,X2=92,Y2=92,StrokeThickness=4,StrokeStartLineCap=PenLineCap.Round,StrokeEndLineCap=PenLineCap.Round,IsHitTestVisible=false };
    SettingsPreviewImage? _wallpaperImage,_customImage;
    Vector2 _magnifierPosition=new(570,190),_magnifierOrigin;
    bool _magnifierDrag,_loadingWallpaper;
    int _loadGeneration;
    readonly Canvas _face=new() { Width=32, Height=32 };
    int _background;
    bool _happyFace, _controlMotion, _darkInk=true, _transportDark=true;
    readonly TextBlock _state = Text("胶囊", 12);
    readonly NumberBox _progressEntry = new() { Minimum=0, Maximum=100, Value=0, SmallChange=1, LargeChange=10, SpinButtonPlacementMode=NumberBoxSpinButtonPlacementMode.Hidden, Width=68, MinWidth=0 };
    readonly TextBlock _hint = Text("拖动空白处移动组件；开启“编辑布局”后可拖动内容。", 12);
    readonly TextBlock _time = Text("09:41", 22), _title = Text("Morning Light", 18), _artist = Text("GlassDesk · 布局示例", 13);
    readonly ToggleButton _edit = new() { Content = "编辑布局", MinHeight = 34 };
    readonly List<Piece> _pieces = [];
    readonly Windows.UI.ViewManagement.AccessibilitySettings _accessibility = new();
    readonly Windows.UI.ViewManagement.UISettings _systemColors = new();
    readonly Border _companionBody = new() { Width = 32, Height = 32, CornerRadius = new(16) };
    readonly SymbolIcon _playIcon = new(Symbol.Play);
    readonly List<Border> _eyes = [];
    readonly List<Microsoft.UI.Xaml.Shapes.Path> _smiles = [];
    readonly Dictionary<(string, bool), Vector2> _offsets = [];
    Vector2 _anchor = new(320, 28), _press, _origin;
    Piece? _dragPiece;
    bool _dragging, _dragExpanded, _active, _disposed, _writing, _roundTrip;
    double? _returnAt;
    WidgetPreferences _preferences = new();
    double Now => _clock.Elapsed.TotalMilliseconds;

    sealed record Piece(string Id, ContentControl Control, Vector2 Capsule, Vector2 Peek, Vector2 Size, bool ExpandedOnly);

    public SettingsLiquidPreview(Func<bool> reduced, Func<Vector2[], Windows.Graphics.IGeometrySource2D?> createGeometry,Func<nint> owner)
    {
        _owner=owner; _reduced = reduced; _createGeometry = createGeometry; Spacing = 12;
        SettingsGlassSlider Make(double min,double max,double value,double step=1) {
            var slider=new SettingsGlassSlider(reduced) { Minimum=min, Maximum=max, Value=value, StepFrequency=step };
            slider.Failed += message => _scope.Text=message; _sliders.Add(slider); return slider;
        }
        _progress=Make(0,100,0); _tension=Make(0,150,75,5); _bounce=Make(0,100,35,5);
        _response=Make(220,800,420,20); _lens=Make(0,150,LiquidTransitionPrototype.MaterialRefraction*100,5); _blur=Make(0,8,.35,.05); _tint=Make(0,30,0);
        _sceneShift=Make(-80,80,0,2);
        _model.Configure(.75f,.35f,420,Now);
        _happy=new SettingsGlassSwitch(reduced); _happy.Failed += message => _scope.Text=message;
        _happy.Changed += value => { _happyFace=value; Render(); };
        _surface.Failed += message => _scope.Text=message;
        var heading = new Grid();
        heading.ColumnDefinitions.Add(new() { Width = new(1, GridUnitType.Star) });
        heading.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        heading.Children.Add(Text("轮廓与布局示意", 17));
        Grid.SetColumn(_state, 1); heading.Children.Add(_state); Children.Add(heading);
        _scope.Opacity=.7; Children.Add(_scope);
        var options=new StackPanel { Orientation=Orientation.Horizontal,Spacing=6 };
        foreach(var choice in new[]{(2,"自定义"),(1,"示例壁纸"),(0,"浅色格栅")}){
            int mode=choice.Item1;
            var button=new ToggleButton { Content=choice.Item2,MinHeight=32,Padding=new(12,4,12,4),CornerRadius=new(8),IsChecked=mode==0 };
            button.Resources["ToggleButtonBackgroundChecked"]=Brush(0xFF1689FA);
            button.Resources["ToggleButtonBackgroundCheckedPointerOver"]=Brush(0xFF1689FA);
            button.Resources["ToggleButtonBackgroundCheckedPressed"]=Brush(0xFF086ED1);
            button.Click+=async(_,_)=>{ if(mode==2&&_customImage is null)await ChooseImage();else if(mode==1&&_wallpaperImage is null)await LoadWallpaper();else SelectBackground(mode); };
            _backgroundButtons.Add(button);options.Children.Add(button);
        }
        _chooseImage.Click+=async(_,_)=>await ChooseImage();options.Children.Add(_chooseImage);
        _opticsOnly.Click+=(_,_)=>{_edit.IsEnabled=_opticsOnly.IsChecked!=true;Render();};
        _lighting.Click+=(_,_)=>Render();
        foreach(var toggle in new[]{_opticsOnly,_lighting}){
            toggle.Resources["ToggleButtonBackgroundChecked"]=Brush(0xFF1689FA);
            toggle.Resources["ToggleButtonBackgroundCheckedPointerOver"]=Brush(0xFF1689FA);
            toggle.Resources["ToggleButtonBackgroundCheckedPressed"]=Brush(0xFF086ED1);
        }
        ToolTipService.SetToolTip(_opticsOnly,"隐藏播放器前景及装饰，只检查背景透镜；布局保持不变");
        ToolTipService.SetToolTip(_lighting,"开关曲面高光与整体投影，不改变透镜采样");
        options.Children.Add(_opticsOnly);options.Children.Add(_lighting);
        Children.Add(options);
        _stage.Background=Brush(0xFFF4F7FB); _stage.Clip=new RectangleGeometry { Rect=new(0,0,640,252) };
        _stage.Children.Add(_surface); _shell.Children.Add(_contents); _stage.Children.Add(_shell);
        var magnifierArt=new Grid { Width=96,Height=96,IsHitTestVisible=false };
        magnifierArt.Children.Add(_magnifierHandle);
        _magnifierLabel.HorizontalAlignment=HorizontalAlignment.Center;_magnifierLabel.VerticalAlignment=VerticalAlignment.Bottom;_magnifierLabel.Margin=new(0,0,0,15);
        magnifierArt.Children.Add(_magnifierLabel);_magnifier.Content=magnifierArt;
        foreach(var key in new[]{"ButtonBackgroundPointerOver","ButtonBackgroundPressed","ButtonBorderBrushPointerOver","ButtonBorderBrushPressed"})_magnifier.Resources[key]=Brush(0);
        AutomationProperties.SetName(_magnifier,"液态玻璃放大镜，可拖动背景透镜，方向键移动");
        _magnifier.Click+=(_,_)=>_magnifier.Focus(FocusState.Programmatic);
        ToolTipService.SetToolTip(_magnifier,"拖动查看背景透镜 · 方向键微调");
        _magnifier.AddHandler(UIElement.PointerPressedEvent,new PointerEventHandler((_,e)=>{if(!e.GetCurrentPoint(_stage).Properties.IsLeftButtonPressed)return;_magnifier.Focus(FocusState.Pointer);_hint.Text="放大镜 · 拖动查看背景";_magnifierDrag=true;_magnifierOrigin=_magnifierPosition;_press=Vector(e.GetCurrentPoint(_stage).Position);_magnifier.CapturePointer(e.Pointer);e.Handled=true;}),true);
        _magnifier.AddHandler(UIElement.PointerMovedEvent,new PointerEventHandler((_,e)=>{if(!_magnifierDrag)return;_magnifierPosition=Vector2.Clamp(_magnifierOrigin+Vector(e.GetCurrentPoint(_stage).Position)-_press,new(48,48),new(592,204));Render();e.Handled=true;}),true);
        void EndMagnifier(object sender,PointerRoutedEventArgs e){_magnifierDrag=false;_magnifier.ReleasePointerCaptures();e.Handled=true;}
        _magnifier.PointerReleased+=EndMagnifier;_magnifier.PointerCanceled+=EndMagnifier;_magnifier.PointerCaptureLost+=EndMagnifier;
        _magnifier.KeyDown+=(_,e)=>{var delta=e.Key switch {VirtualKey.Left=>new Vector2(-4,0),VirtualKey.Right=>new Vector2(4,0),VirtualKey.Up=>new Vector2(0,-4),VirtualKey.Down=>new Vector2(0,4),_=>Vector2.Zero};if(delta==Vector2.Zero)return;_magnifierPosition=Vector2.Clamp(_magnifierPosition+delta,new(48,48),new(592,204));Render();e.Handled=true;};
        _stage.Children.Add(_magnifier);
        var view=new Viewbox { Child=_stage, Stretch=Stretch.Uniform, HorizontalAlignment=HorizontalAlignment.Stretch };
        Children.Add(new Border { Child=view, CornerRadius=new(12), Background=Brush(0xFFF4F7FB) });

        var toolbar = new Grid { ColumnSpacing = 8 };
        for (int i = 0; i < 5; i++) toolbar.ColumnDefinitions.Add(new() { Width = new(1, GridUnitType.Star) });
        var collapse = Button("胶囊", () => Target(false)); var expand = Button("小展开", () => Target(true));
        var replay = Button("播放往返", Replay); var reset = Button("重置布局", ResetLayout);
        FrameworkElement[] commands = [collapse, expand, replay, _edit, reset];
        for (int i = 0; i < commands.Length; i++) { Grid.SetColumn(commands[i], i); toolbar.Children.Add(commands[i]); }
        Children.Add(toolbar);
        var progressRow = new Grid { ColumnSpacing = 12 };
        progressRow.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        progressRow.ColumnDefinitions.Add(new() { Width = new(1, GridUnitType.Star) });
        progressRow.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        progressRow.Children.Add(Text("展开进度", 12)); Grid.SetColumn(_progress, 1); progressRow.Children.Add(_progress);
        var progressEntry=new StackPanel { Orientation=Orientation.Horizontal, Spacing=4 }; progressEntry.Children.Add(_progressEntry); progressEntry.Children.Add(Text("%",12));
        Grid.SetColumn(progressEntry,2); progressRow.Children.Add(progressEntry); Children.Add(progressRow);
        AutomationProperties.SetName(_progressEntry,"精确展开进度百分比");
        _progressEntry.ValueChanged += (_,args) => { if(_writing || !double.IsFinite(args.NewValue))return; CancelPlayback(); _model.Scrub(args.NewValue/100); Render(); };
        var tuning = new Grid { ColumnSpacing=18, RowSpacing=6 };
        for(int i=0;i<3;i++) tuning.ColumnDefinitions.Add(new() { Width=new(1,GridUnitType.Star) });
        for(int row=0;row<3;row++) tuning.RowDefinitions.Add(new() { Height=GridLength.Auto });
        var controls=new[]{ Parameter("轮廓张力",_tension,()=>$"{_tension.Value:0}%"),
            Parameter("回弹",_bounce,()=>$"{_bounce.Value:0}%"), Parameter("响应",_response,()=>$"{_response.Value:0} ms"),
            Parameter("边缘折射",_lens,()=>$"{_lens.Value:0}%"),
            Parameter("背景横移",_sceneShift,()=>$"{_sceneShift.Value:0} DIP"), Parameter("本地背景柔化",_blur,()=>$"{_blur.Value:0.00}"),
            Parameter("本地白色覆盖",_tint,()=>$"{_tint.Value:0}%") };
        for(int i=0;i<controls.Length;i++) { Grid.SetColumn(controls[i],i%3); Grid.SetRow(controls[i],i/3); tuning.Children.Add(controls[i]); }
        Children.Add(tuning);
        var companionRow=new Grid(); companionRow.ColumnDefinitions.Add(new() { Width=new(1,GridUnitType.Star) });
        companionRow.ColumnDefinitions.Add(new() { Width=GridLength.Auto }); companionRow.Children.Add(Text("陪伴开心表情 · 仅本页",12));
        Grid.SetColumn(_happy,1); companionRow.Children.Add(_happy); Children.Add(companionRow); Children.Add(_hint);
        _progress.AccessibleName="展开进度，可拖动暂停查看中间轮廓";
        _tension.AccessibleName="轮廓张力"; _bounce.AccessibleName="回弹"; _response.AccessibleName="响应时间";
        _lens.AccessibleName="边缘折射"; _sceneShift.AccessibleName="背景横移";
        _blur.AccessibleName="本地背景柔化半径，仅影响本页示例背景"; _tint.AccessibleName="本地白色覆盖，仅影响本页示例背景";
        foreach(var slider in new[]{_lens,_blur,_tint,_sceneShift}) slider.ValueChanged += (_,_) => Render();
        _edit.Checked += (_, _) => EditingChanged(); _edit.Unchecked += (_, _) => EditingChanged();
        _progress.ValueChanged += (_, _) => { if (_writing) return; CancelPlayback(); _model.Scrub(_progress.Value / 100); Render(); };
        foreach (var slider in new[] { _tension, _bounce, _response })
            slider.ValueChanged += (_, _) => { _model.Configure((float)_tension.Value / 100, (float)_bounce.Value / 100, (int)_response.Value, Now); Render(); Wake(); };
        _timer = DispatcherQueue.GetForCurrentThread().CreateTimer(); _timer.Interval = TimeSpan.FromMilliseconds(16);
        _timer.Tick += Tick;
        Loaded += (_, _) =>
        {
            var visual = Microsoft.UI.Xaml.Hosting.ElementCompositionPreview.GetElementVisual(_contents);
            _contentGeometry ??= visual.Compositor.CreatePathGeometry();
            visual.Clip = visual.Compositor.CreateGeometricClip(_contentGeometry);
            Render();
            if(!_loadingWallpaper){_loadingWallpaper=true;_ = LoadWallpaper();}
        };
        Unloaded += (_, _) => SetActive(false);
        BuildPieces();
        _stage.PointerPressed += BodyPressed; _stage.PointerMoved += DragMoved; _stage.PointerReleased += EndDrag;
        _stage.PointerCanceled += EndDrag; _stage.PointerCaptureLost += EndDrag;
        Render();
    }

    void BuildPieces()
    {
        var face = _face; face.Children.Add(_companionBody);
        for (int i = 0; i < 2; i++)
        {
            var eye = new Border { Width = 4, Height = 6, CornerRadius = new(2), Background = Brush(0xFFFFFFFF) };
            Canvas.SetLeft(eye, i == 0 ? 9 : 19); Canvas.SetTop(eye, 13); face.Children.Add(eye); _eyes.Add(eye);
            var smile=new Microsoft.UI.Xaml.Shapes.Path { Stroke=Brush(0xFFFFFFFF), StrokeThickness=2,
                StrokeStartLineCap=PenLineCap.Round, StrokeEndLineCap=PenLineCap.Round, Visibility=Visibility.Collapsed,
                Data=new PathGeometry { Figures={new PathFigure { StartPoint=new(0,3),
                    Segments={new QuadraticBezierSegment { Point1=new(3,-2),Point2=new(6,3) }} }} } };
            face.Children.Add(smile); _smiles.Add(smile);
        }
        Add("陪伴球", face, new(78, 10), new(74, 12), new(58, 32));
        SettingsTypography.Apply(_time, TextRole.ClockCapsule, InkRole.Primary);
        _time.TextAlignment = TextAlignment.Center; _time.VerticalAlignment = VerticalAlignment.Center;
        _time.HorizontalAlignment=HorizontalAlignment.Center;
        Add("时间", _time, new(118, 10), new(142, 10), new(124, 32));
        var image = new Image { Source = new BitmapImage(new Uri("ms-appx:///Assets/GlassDesk-logo.png")), Stretch = Stretch.Uniform };
        Add("封面", new Border { Child = image, CornerRadius = new(12), Background = Brush(0xE8FFFFFF), Padding = new(2) },
            new(82, 12), new(24, 60), new(48, 48), true);
        _title.FontWeight = Microsoft.UI.Text.FontWeights.SemiBold; _title.TextTrimming = TextTrimming.CharacterEllipsis;
        _title.HorizontalAlignment=_artist.HorizontalAlignment=HorizontalAlignment.Left;
        _artist.Opacity = 1;
        var metadata = new StackPanel { Spacing = 3 }; metadata.Children.Add(_title); metadata.Children.Add(_artist);
        Add("歌曲文字", metadata, new(126, 14), new(88, 61), new(174, 48), true);
        var transport = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        foreach (var glyph in new[] { Symbol.Previous, Symbol.Play, Symbol.Next })
        {
            bool play = transport.Children.Count == 1;
            var label = play ? _playIcon : new SymbolIcon(glyph);
            var button = new Microsoft.UI.Xaml.Controls.Button { Content = label, Width = 42, Height = 42,
                MinWidth = 0, MinHeight = 0, Padding = new(0), CornerRadius = new(13),
                Background = Brush(0), BorderBrush = Brush(0), BorderThickness = new(0) };
            AutomationProperties.SetName(button, play ? "预览播放或暂停" : transport.Children.Count == 0 ? "预览上一首" : "预览下一首");
            if (play) button.Click += (_, _) => _playIcon.Symbol = _playIcon.Symbol == Symbol.Play ? Symbol.Pause : Symbol.Play;
            foreach(var key in new[]{"ButtonBackgroundPointerOver","ButtonBackgroundPressed","ButtonBorderBrushPointerOver","ButtonBorderBrushPressed"}) button.Resources[key]=Brush(0);
            int index=transport.Children.Count;
            void Feedback(float value) { _buttonTarget[index]=value; _controlMotion=true; if(_reduced())_buttonActivity[index]=value; Render(); Wake(); }
            button.PointerEntered += (_,_) => Feedback(.22f); button.PointerExited += (_,_) => Feedback(0);
            button.AddHandler(UIElement.PointerPressedEvent,new PointerEventHandler((_,_)=>Feedback(1)),true);
            button.AddHandler(UIElement.PointerReleasedEvent,new PointerEventHandler((_,_)=>Feedback(0)),true);
            button.PointerCaptureLost += (_,_) => Feedback(0); button.LostFocus += (_,_) => Feedback(0);
            button.KeyDown += (_,e) => { if(e.Key is VirtualKey.Space or VirtualKey.Enter)Feedback(1); };
            button.KeyUp += (_,_) => Feedback(0);
            transport.Children.Add(button);
        }
        Add("播放控制", transport, new(108, 12), new(89, 120), new(142, 42), true);
    }

    void Add(string id, UIElement content, Vector2 capsule, Vector2 peek, Vector2 size, bool expandedOnly = false)
    {
        var border = new Border { Child = content, BorderThickness = new(1), CornerRadius = new(8), BorderBrush = Brush(0), Background = Brush(0x00FFFFFF) };
        var root = new ContentControl { Content = border, Width = size.X + 2, Height = size.Y + 2,
            IsTabStop = true, UseSystemFocusVisuals = true, HorizontalContentAlignment = HorizontalAlignment.Stretch,
            VerticalContentAlignment = VerticalAlignment.Stretch };
        var piece = new Piece(id, root, capsule, peek, size + new Vector2(2), expandedOnly); _pieces.Add(piece); _contents.Children.Add(root);
        AutomationProperties.SetName(root, id + "布局预览，编辑时可拖动，方向键微调");
        ToolTipService.SetToolTip(root, id);
        root.AddHandler(UIElement.PointerPressedEvent, new PointerEventHandler((_, e) => PiecePressed(piece, e)), true);
        root.PointerMoved += DragMoved; root.PointerReleased += EndDrag; root.PointerCanceled += EndDrag; root.PointerCaptureLost += EndDrag;
        root.KeyDown += (_, e) => MoveWithKeyboard(piece, e);
    }

    public void Present(WidgetPreferences preferences)
    {
        if (_disposed) return;
        _preferences = preferences;
        _companionBody.Background = Brush(preferences.CompanionColor);
        double r = (preferences.CompanionColor >> 16 & 255) / 255d, g = (preferences.CompanionColor >> 8 & 255) / 255d, b = (preferences.CompanionColor & 255) / 255d;
        foreach (var eye in _eyes) eye.Background = Brush(.2126 * r * r + .7152 * g * g + .0722 * b * b > .2 ? 0xFF202329 : 0xFFFFFFFF);
        for(int i=0;i<_smiles.Count;i++) _smiles[i].Stroke=_eyes[i].Background;
        _time.Text = DateTime.Now.ToString("HH:mm"); SettingsTypography.SetClockFamily(_time, preferences.ClockFontId);
        if (_reduced() && _model.Active) { _model.Scrub(_model.Target); CancelPlayback(); }
        if (_active) Render();
    }

    public void SetActive(bool active)
    {
        if (_disposed) return;
        _active = active;
        _surface.SetActive(active); foreach(var slider in _sliders)slider.SetActive(active); _happy.SetActive(active);
        if(!active) { _magnifierDrag=false;_magnifier.ReleasePointerCaptures(); Array.Clear(_buttonActivity); Array.Clear(_buttonTarget); _controlMotion=false; }
        if (!active) { CancelPlayback(); ReleaseDrag(); _model.Scrub(Math.Clamp(_model.Progress, 0, 1)); }
        else { Render(); Wake(); }
    }
    void Target(bool expanded)
    { CancelPlayback(); _model.SetTarget(expanded, Now, _reduced()); Render(); Wake(); }
    void Replay()
    {
        CancelPlayback(); _model.Scrub(0); _roundTrip = !_reduced();
        _model.SetTarget(true, Now, _reduced()); Render(); Wake();
    }
    void Tick(DispatcherQueueTimer sender, object args)
    {
        if (!_active || _disposed) { sender.Stop(); return; }
        _model.Sample(Now);
        _controlMotion=false;
        for(int i=0;i<3;i++) { _buttonActivity[i]+=(_buttonTarget[i]-_buttonActivity[i])*.34f;
            if(Math.Abs(_buttonActivity[i]-_buttonTarget[i])<.008f)_buttonActivity[i]=_buttonTarget[i]; else _controlMotion=true; }
        if (_roundTrip && !_model.Active && _model.Target == 1)
        {
            _returnAt ??= Now + 300;
            if (Now >= _returnAt) { _roundTrip = false; _returnAt = null; _model.SetTarget(false, Now); }
        }
        Render(); if (!_model.Active && !_roundTrip && !_controlMotion) sender.Stop();
    }
    void Wake() { if (_active && !_disposed && (_model.Active || _roundTrip || _controlMotion)) _timer.Start(); }
    void CancelPlayback() { _timer.Stop(); _roundTrip = false; _returnAt = null; }

    void Render()
    {
        if (_disposed) return;
        UpdateInk();
        _contents.Visibility=_opticsOnly.IsChecked==true?Visibility.Collapsed:Visibility.Visible;
        var points = _model.Contour(28);

        if (_contentGeometry is not null && _createGeometry(points.Select(p => p + new Vector2(16)).ToArray()) is { } source)
            _contentGeometry.Path = new Microsoft.UI.Composition.CompositionPath(source);
        Canvas.SetLeft(_shell, _anchor.X - 176); Canvas.SetTop(_shell, _anchor.Y - 16);
        float p = _model.HeaderProgress, c = _model.ContentProgress;
        var faceSize=_model.CompanionSize; _face.Width=_companionBody.Width=faceSize.X; _face.Height=_companionBody.Height=faceSize.Y;
        _companionBody.CornerRadius=new(faceSize.Y/2);
        for(int i=0;i<_eyes.Count;i++) { var eye=_eyes[i]; Canvas.SetLeft(eye,faceSize.X/2+(i==0?-6:6)-2+Math.Clamp(_model.Velocity*.22,-1.2,1.2));
            Canvas.SetTop(eye,faceSize.Y/2-(_happyFace?1:3)); eye.Height=6; eye.Visibility=_happyFace?Visibility.Collapsed:Visibility.Visible;
            var smile=_smiles[i]; Canvas.SetLeft(smile,faceSize.X/2+(i==0?-6:6)-3); Canvas.SetTop(smile,faceSize.Y/2-3);
            smile.Visibility=_happyFace?Visibility.Visible:Visibility.Collapsed; }
        var frame=new float[64]; frame[3]=0; frame[4]=_anchor.X; frame[5]=_anchor.Y; frame[6]=(float)_model.Progress;
        frame[7]=(float)_model.Velocity; frame[8]=_model.Tension; frame[9]=(float)_lens.Value/100;
        frame[10]=(float)_blur.Value; frame[11]=(float)_tint.Value/100; frame[12]=LiquidTransitionPrototype.MaterialMagnification; frame[46]=(float)_sceneShift.Value; frame[13]=_model.TransportProgress; frame[14]=1; frame[15]=_background;
        frame[47]=(_background==1?_wallpaperImage:_background==2?_customImage:null) is null?0:1;
        frame[60]=_lighting.IsChecked==true?1:0;frame[62]=_opticsOnly.IsChecked==true?1:0;
        frame[28]=_magnifierPosition.X;frame[29]=_magnifierPosition.Y;frame[30]=88;frame[31]=88;frame[44]=44;frame[45]=.5f;
        Canvas.SetLeft(_magnifier,_magnifierPosition.X-48);Canvas.SetTop(_magnifier,_magnifierPosition.Y-48);
        _magnifierLabel.Foreground=ForegroundInk();_magnifierHandle.Stroke=_magnifierLabel.Foreground;
        foreach (var piece in _pieces)
        {
            Vector2 start = piece.Capsule + Offset(piece, false), end = piece.Peek + Offset(piece, true);
            float reveal=piece.Id=="播放控制"?_model.TransportProgress:c;
            var pos = piece.ExpandedOnly ? end+new Vector2(0,8*(1-reveal)) : Vector2.Lerp(start,end,p);
            if(piece.Id=="陪伴球") { pos+=_model.HeaderOffset(true); piece.Control.Width=faceSize.X+2; piece.Control.Height=faceSize.Y+2; }
            if(piece.Id=="时间") { pos+=_model.HeaderOffset(false); piece.Control.Width=double.Lerp(124,104,p); }
            if(piece.Id=="播放控制" && reveal>.001f)for(int i=0;i<3;i++) {
                frame[16+i*4]=_anchor.X-160+pos.X+22+i*50; frame[17+i*4]=_anchor.Y+pos.Y+22;
                frame[18+i*4]=42; frame[19+i*4]=42; frame[32+i*4]=21; frame[33+i*4]=_buttonActivity[i]*c;
            }
            if(piece.Id=="时间")SetInk(_time,false);
            if(piece.Id=="歌曲文字") { SetInk(_title,false);SetInk(_artist,true); }
            if(piece.Id=="播放控制") {
                _transportDark=_darkInk;
                for(int i=0;i<3;i++)frame[34+i*4]=_transportDark?1:0;
            }
            if(piece.Id=="播放控制" && piece.Control.Content is Border transportBorder && transportBorder.Child is StackPanel buttons)
                for(int i=0;i<buttons.Children.Count;i++)if(buttons.Children[i] is Microsoft.UI.Xaml.Controls.Button button && button.Content is SymbolIcon icon)
                    icon.Foreground=ForegroundInk();
            Canvas.SetLeft(piece.Control, pos.X + 16); Canvas.SetTop(piece.Control, pos.Y + 16);
            piece.Control.Opacity = piece.ExpandedOnly ? reveal : 1;
            piece.Control.IsHitTestVisible = !piece.ExpandedOnly || reveal > .85;
            piece.Control.IsTabStop=!piece.ExpandedOnly || reveal>.85;
            if(piece.ExpandedOnly)piece.Control.Visibility=reveal>.001f?Visibility.Visible:Visibility.Collapsed;
            if (piece.Id == "陪伴球") piece.Control.Visibility = _preferences.CompanionEnabled ? Visibility.Visible : Visibility.Collapsed;
        }
        _surface.Present(frame);
        _writing = true; _progress.Value = Math.Clamp(_model.Progress * 100, 0, 100); _progressEntry.Value=Math.Round(_progress.Value); _writing = false;
        _state.Text = _model.Active ? _model.Target == 1 ? "展开中" : "收拢中" : _model.Progress >= .999 ? "小展开" : _model.Progress <= .001 ? "胶囊" : "中间形态";
        if(_reduced())_state.Text+=" · 简化动画";
    }

    void SelectBackground(int mode)
    {
        _background=mode;
        for(int i=0;i<_backgroundButtons.Count;i++)_backgroundButtons[i].IsChecked=(i==0?2:i==1?1:0)==mode;
        _surface.SetImage(mode==1?_wallpaperImage:mode==2?_customImage:null);
        _scope.Text=mode==0?"背景为本页浅色格栅；实际材质以桌面组件为准。":"背景为本地图片；实际材质以桌面组件为准。";
        Render();
    }
    async Task LoadWallpaper()
    {
        try {
            var image=await SettingsPreviewImage.Load(System.IO.Path.Combine(AppContext.BaseDirectory,"PreviewAssets","Apple-Flower-5.jpg"));
            if(_disposed)return;_wallpaperImage=image;if(_loadGeneration==0&&_background==0&&_customImage is null)SelectBackground(1);
        }catch(Exception){if(!_disposed){SelectBackground(0);_scope.Text="示例壁纸尚未放入预览目录，可以选择自定义图片。";}}
    }
    async Task ChooseImage()
    {
        int generation=++_loadGeneration;_chooseImage.IsEnabled=false;
        try {
            await Task.Yield();
            var path=SettingsPreviewImage.PickFile(_owner());
            if(path is null||_disposed)return;
            var image=await SettingsPreviewImage.Load(path);
            if(_disposed||generation!=_loadGeneration)return;
            _customImage=image;SelectBackground(2);
        }catch(Exception error){if(!_disposed)_scope.Text=$"图片未能载入（0x{error.HResult:X8}）："+error.Message;}
        finally{if(!_disposed){_chooseImage.IsEnabled=true;for(int i=0;i<_backgroundButtons.Count;i++)_backgroundButtons[i].IsChecked=(i==0?2:i==1?1:0)==_background;}}
    }

    Vector2 Offset(Piece piece, bool expanded) => _offsets.GetValueOrDefault((piece.Id, expanded));
    void EditingChanged()
    {
        ReleaseDrag();
        foreach (var piece in _pieces)
        {
            var border = (Border)piece.Control.Content;
            border.BorderBrush = Brush(_edit.IsChecked == true ? 0x6B1689F9u : 0u);
            border.Child.IsHitTestVisible = _edit.IsChecked != true;
        }
        _hint.Text = _edit.IsChecked == true ? "拖动内容调整布局；选中后可用方向键微调。" : "拖动空白处移动组件；开启“编辑布局”后可拖动内容。";
    }
    void ResetLayout()
    { ReleaseDrag(); _offsets.Clear(); _anchor = new(320, 28); Render(); _hint.Text = "布局与位置已重置。"; }
    void PiecePressed(Piece piece, PointerRoutedEventArgs e)
    {
        if (_edit.IsChecked != true || !e.GetCurrentPoint(_stage).Properties.IsLeftButtonPressed) return;
        CancelPlayback(); _model.Scrub(_model.Progress >= .5 ? 1 : 0); Render();
        _dragPiece = piece; _dragExpanded = _model.Progress >= .5;
        _origin = ( _dragExpanded ? piece.Peek : piece.Capsule ) + Offset(piece, _dragExpanded);
        _press = Vector(e.GetCurrentPoint(_stage).Position);
        _dragging = piece.Control.CapturePointer(e.Pointer);
        piece.Control.Focus(FocusState.Pointer); e.Handled = true;
    }
    void BodyPressed(object sender, PointerRoutedEventArgs e)
    {
        if (e.Handled || !e.GetCurrentPoint(_stage).Properties.IsLeftButtonPressed) return;
        var point = Vector(e.GetCurrentPoint(_stage).Position);
        if (_model.Distance(point - new Vector2(_anchor.X - 160, _anchor.Y)) > 0) return;
        _dragPiece = null; _origin = _anchor; _press = point; _dragging = _stage.CapturePointer(e.Pointer); e.Handled = true;
    }
    void DragMoved(object sender, PointerRoutedEventArgs e)
    {
        if (!_dragging) return;
        Vector2 delta = Vector(e.GetCurrentPoint(_stage).Position) - _press;
        if (_dragPiece is { } piece)
        {
            var pos = LiquidTransitionPrototype.ClampPlacement(_origin + delta, piece.Size, _dragExpanded);
            _offsets[(piece.Id, _dragExpanded)] = pos - (_dragExpanded ? piece.Peek : piece.Capsule);
            _hint.Text = $"{piece.Id}  ·  X {pos.X:0}   Y {pos.Y:0}";
        }
        else _anchor = Vector2.Clamp(_origin + delta, new(176, 14), new(464, 62));
        Render(); e.Handled = true;
    }
    void EndDrag(object sender, PointerRoutedEventArgs e) { if (!_dragging) return; ReleaseDrag(); e.Handled = true; }
    void ReleaseDrag()
    { _dragging = false; _dragPiece?.Control.ReleasePointerCaptures(); _stage.ReleasePointerCaptures(); _dragPiece = null; }
    void MoveWithKeyboard(Piece piece, KeyRoutedEventArgs e)
    {
        if (_edit.IsChecked != true) return;
        Vector2 step = e.Key switch { VirtualKey.Left => new(-1, 0), VirtualKey.Right => new(1, 0), VirtualKey.Up => new(0, -1), VirtualKey.Down => new(0, 1), _ => Vector2.Zero };
        if (step == Vector2.Zero) return;
        CancelPlayback(); bool expanded = _model.Progress >= .5; _model.Scrub(expanded ? 1 : 0);
        var origin = expanded ? piece.Peek : piece.Capsule;
        var pos = LiquidTransitionPrototype.ClampPlacement(origin + Offset(piece, expanded) + step * 2, piece.Size, expanded);
        _offsets[(piece.Id, expanded)] = pos - origin; Render(); _hint.Text = $"{piece.Id}  ·  X {pos.X:0}   Y {pos.Y:0}"; e.Handled = true;
    }

    StackPanel Parameter(string title, SettingsGlassSlider slider, Func<string> value)
    {
        var panel = new StackPanel { Spacing = 2 }; var label = Text(title + "  " + value(), 12);
        panel.Children.Add(label); panel.Children.Add(slider); slider.ValueChanged += (_, _) => label.Text = title + "  " + value(); return panel;
    }
    static Microsoft.UI.Xaml.Controls.Button Button(string title, Action action)
    {
        var button = new Microsoft.UI.Xaml.Controls.Button { Content = title, MinHeight = 34, Padding = new(8, 4, 8, 4),
            HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Center, CornerRadius = new(8) };
        button.Click += (_, _) => action(); return button;
    }
    static TextBlock Text(string text, double size) => new() { Text = text, FontSize = size, Foreground = Brush(0xFF27313E),
        FontFamily = new FontFamily("Segoe UI Variable, Microsoft YaHei UI"), TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center };
    internal static SolidColorBrush Brush(uint color) => new(Color.FromArgb((byte)(color >> 24), (byte)(color >> 16), (byte)(color >> 8), (byte)color));
    void UpdateInk() => _darkInk = _preferences.TextInk != WidgetTextInk.White;
    Brush ForegroundInk(bool secondary = false)
    {
        if (_accessibility.HighContrast)
            return new SolidColorBrush(_systemColors.UIElementColor(Windows.UI.ViewManagement.UIElementType.WindowText));
        return Brush(TypographyCatalog.Ink(secondary ? InkRole.Secondary : InkRole.Primary, !_darkInk));
    }
    void SetInk(TextBlock text, bool secondary) => text.Foreground = ForegroundInk(secondary);
    static Vector2 Vector(Point p) => new((float)p.X, (float)p.Y);
    public void Dispose()
    { if (_disposed) return; SetActive(false); _disposed = true; _timer.Stop(); _timer.Tick -= Tick; _surface.Dispose(); foreach(var slider in _sliders)slider.Dispose(); _happy.Dispose(); }
}

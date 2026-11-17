using System;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace LovenseConnector
{
    /// <summary>A native, one-lane pattern editor. No playback or hardware side effects.</summary>
    public sealed class CurveEditorControl : Control
    {
        public static readonly DependencyProperty DocumentProperty = DependencyProperty.Register(
            nameof(Document), typeof(CurveDocument), typeof(CurveEditorControl),
            new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender, DocumentChanged));
        public static readonly DependencyProperty ActiveLaneIdProperty = DependencyProperty.Register(
            nameof(ActiveLaneId), typeof(string), typeof(CurveEditorControl),
            new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender, LaneChanged));
        public static readonly DependencyProperty IsReadOnlyProperty = DependencyProperty.Register(
            nameof(IsReadOnly), typeof(bool), typeof(CurveEditorControl),
            new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsRender));

        public CurveDocument Document { get => (CurveDocument)GetValue(DocumentProperty); set => SetValue(DocumentProperty, value); }
        public string ActiveLaneId { get => (string)GetValue(ActiveLaneIdProperty); set => SetValue(ActiveLaneIdProperty, value); }
        public bool IsReadOnly { get => (bool)GetValue(IsReadOnlyProperty); set => SetValue(IsReadOnlyProperty, value); }
        public event EventHandler SelectionChanged;
        public event EventHandler StopRequested;
        public event Action<string> EditError;

        int selected = -1;
        bool dragging, finishing;
        CurveDocument dragDocument;
        string dragLane;
        int dragIndex;
        public int SelectedPointIndex
        {
            get => selected;
            set
            {
                var lane = ActiveLane;
                int next = lane == null || value < 0 || value >= lane.Points.Count ? -1 : value;
                if (next == selected) return;
                int previousLevel = SelectedPoint?.Level ?? 0;
                selected = next; InvalidateVisual(); SelectionChanged?.Invoke(this, EventArgs.Empty);
                if (UIElementAutomationPeer.FromElement(this) is CurveEditorPeer peer)
                    peer.SelectionUpdated(previousLevel, SelectedPoint?.Level ?? 0);
            }
        }
        public CurvePoint? SelectedPoint => ActiveLane != null && selected >= 0 && selected < ActiveLane.Points.Count
            ? ActiveLane.Points[selected] : (CurvePoint?)null;
        CurveLaneModel ActiveLane => Document != null && Document.ContainsLane(ActiveLaneId) ? Document.GetLane(ActiveLaneId) : null;
        bool CanEdit => IsEnabled && !IsReadOnly && ActiveLane != null;
        static readonly Brush TextBrush = MakeBrush("#FAF7FF"), MutedBrush = MakeBrush("#C4BAD7"),
            SurfaceBrush = MakeBrush("#B00A0715"), GridBrush = MakeBrush("#303F3154");
        static readonly Color[] LaneColors = { (Color)ColorConverter.ConvertFromString("#CCB8FF"),
            (Color)ColorConverter.ConvertFromString("#A7DDF5"), (Color)ColorConverter.ConvertFromString("#E5A8CC") };

        public CurveEditorControl()
        {
            Focusable = true; IsTabStop = true; FocusVisualStyle = null;
            MinHeight = 200; MinWidth = 220; ClipToBounds = true;
            SnapsToDevicePixels = true; UseLayoutRounding = true;
            ToolTip = "Double-click to add a point. Drag to edit. Arrows change time or level; Shift makes larger changes. Page Up/Down selects points. Insert adds, Delete removes. Ctrl+Z undoes.";
            AutomationProperties.SetName(this, "Pattern curve editor");
            AutomationProperties.SetHelpText(this, (string)ToolTip);
            Unloaded += (sender, args) => FinishDrag(true);
        }

        static Brush MakeBrush(string color)
        { var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(color)); brush.Freeze(); return brush; }
        static void DocumentChanged(DependencyObject sender, DependencyPropertyChangedEventArgs args)
        {
            var editor = (CurveEditorControl)sender; editor.FinishDrag(true);
            if (args.OldValue is CurveDocument previous) previous.Changed -= editor.OnDocumentChanged;
            if (args.NewValue is CurveDocument next) next.Changed += editor.OnDocumentChanged;
            editor.ClearSelection(); editor.EnsureLane(); editor.InvalidateVisual();
        }
        static void LaneChanged(DependencyObject sender, DependencyPropertyChangedEventArgs args)
        { var editor = (CurveEditorControl)sender; editor.FinishDrag(true); editor.ClearSelection(); }
        void EnsureLane()
        {
            if (Document != null && !Document.ContainsLane(ActiveLaneId))
                SetCurrentValue(ActiveLaneIdProperty, Document.Lanes.FirstOrDefault()?.Id);
        }
        void OnDocumentChanged(object sender, EventArgs args)
        {
            EnsureLane();
            if (selected >= (ActiveLane?.Points.Count ?? 0)) SelectedPointIndex = -1;
            InvalidateVisual();
        }
        public void ClearSelection() => SelectedPointIndex = -1;
        public void Refresh() { EnsureLane(); InvalidateVisual(); }

        protected override Size MeasureOverride(Size available) => new Size(
            double.IsInfinity(available.Width) ? 620 : Math.Max(0, available.Width),
            double.IsInfinity(available.Height) ? 290 : Math.Max(0, available.Height));

        Rect Plot => new Rect(46, 48, Math.Max(1, ActualWidth - 66), Math.Max(1, ActualHeight - 96));
        Point Position(CurvePoint point)
        {
            var plot = Plot;
            return new Point(plot.Left + plot.Width * point.Ms / Document.LengthMs,
                plot.Bottom - plot.Height * point.Level / 20.0);
        }
        int TimeAt(double x) => (int)Math.Round(Math.Clamp((x - Plot.Left) / Plot.Width, 0, 1) * Document.LengthMs,
            MidpointRounding.AwayFromZero);
        int LevelAt(double y) => Math.Clamp((int)Math.Round((Plot.Bottom - y) / Plot.Height * 20,
            MidpointRounding.AwayFromZero), 0, 20);
        string Seconds(double ms) => (ms / 1000).ToString("0.###", CultureInfo.InvariantCulture) + " s";
        void Label(DrawingContext context, string value, double x, double y, double size, Brush brush, bool bold = false, double maxWidth = double.PositiveInfinity)
        {
            var typeface = new Typeface(FontFamily, FontStyles.Normal, bold ? FontWeights.SemiBold : FontWeights.Normal, FontStretches.Normal);
            var text = new FormattedText(value, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, typeface, size, brush,
                VisualTreeHelper.GetDpi(this).PixelsPerDip);
            if (double.IsFinite(maxWidth)) { text.MaxTextWidth = Math.Max(1, maxWidth); text.MaxLineCount = 1; text.Trimming = TextTrimming.CharacterEllipsis; }
            context.DrawText(text, new Point(x, y));
        }

        protected override void OnRender(DrawingContext context)
        {
            base.OnRender(context);
            if (ActualWidth < 1 || ActualHeight < 1) return;
            var outer = new Rect(1, 1, Math.Max(0, ActualWidth - 2), Math.Max(0, ActualHeight - 2));
            context.DrawRoundedRectangle(Background ?? SurfaceBrush, null, outer, 18, 18);
            var lane = ActiveLane;
            if (lane == null)
            {
                Label(context, "Select a motor to edit its curve.", 20, 22, 16, MutedBrush);
                DrawFocus(context, outer); return;
            }
            int laneIndex = Document.Lanes.ToList().FindIndex(item => item.Id == lane.Id);
            var color = LaneColors[Math.Max(0, laneIndex) % LaneColors.Length];
            var accent = new SolidColorBrush(color);
            var plot = Plot;
            Label(context, lane.DisplayName, 18, 14, 17, TextBrush, true, ActualWidth - (ActualWidth >= 420 ? 190 : 36));
            string state = !lane.IsAvailable ? "Disconnected" : Document.LinkedLaneIds(lane.Id).Length > 1
                ? "Linked · " + Document.LinkedLaneIds(lane.Id).Length + " motors" : "Levels 0–20";
            if (ActualWidth >= 420) Label(context, state, ActualWidth - 158, 17, 12, MutedBrush, false, 144);
            var gridPen = new Pen(GridBrush, 1);
            for (int level = 0; level <= 20; level += 5)
            {
                double y = plot.Bottom - plot.Height * level / 20.0;
                context.DrawLine(gridPen, new Point(plot.Left, y), new Point(plot.Right, y));
                if (level % 10 == 0) Label(context, level.ToString(CultureInfo.InvariantCulture), 13, y - 8, 12, MutedBrush);
            }
            int ticks = plot.Width < 360 ? 4 : 6;
            for (int i = 0; i <= ticks; i++)
            {
                double x = plot.Left + plot.Width * i / ticks;
                context.DrawLine(gridPen, new Point(x, plot.Top), new Point(x, plot.Bottom));
                string value = Seconds(Document.LengthMs * (double)i / ticks);
                double labelX = i == ticks ? Math.Max(plot.Left, x - value.Length * 6.2) : x - (i == 0 ? 0 : value.Length * 3.0);
                Label(context, value, labelX, plot.Bottom + 10, 12, MutedBrush);
            }

            var line = BuildCurve(lane, false);
            var fill = BuildCurve(lane, true);
            var shade = new LinearGradientBrush(Color.FromArgb(52, color.R, color.G, color.B),
                Color.FromArgb(3, color.R, color.G, color.B), 90);
            context.DrawGeometry(shade, null, fill);
            var pen = new Pen(accent, 2.5);
            if (laneIndex % 3 == 1) pen.DashStyle = DashStyles.Dash;
            if (laneIndex % 3 == 2) pen.DashStyle = DashStyles.Dot;
            context.DrawGeometry(null, pen, line);
            if (!IsReadOnly)
            {
                for (int i = 0; i < lane.Points.Count; i++)
                {
                    var point = Position(lane.Points[i]);
                    if (i == selected)
                    {
                        context.DrawEllipse(new SolidColorBrush(Color.FromArgb(42, color.R, color.G, color.B)), null, point, 16, 16);
                        context.DrawEllipse(null, new Pen(accent, 2), point, 11, 11);
                    }
                    DrawNode(context, point, i == selected ? 6 : 4.5, laneIndex % 3, accent);
                }
            }
            DrawFocus(context, outer);
        }

        Geometry BuildCurve(CurveLaneModel lane, bool fill)
        {
            var geometry = new StreamGeometry();
            using (var drawing = geometry.Open())
            {
                var points = lane.Points; var first = Position(points[0]);
                drawing.BeginFigure(first, fill, fill);
                for (int i = 1; i < points.Count; i++)
                {
                    var a = Position(points[i - 1]); var b = Position(points[i]);
                    if (lane.Interpolation == CurveInterpolation.Step)
                    { drawing.LineTo(new Point(b.X, a.Y), true, false); drawing.LineTo(b, true, false); }
                    else if (lane.Interpolation == CurveInterpolation.Smooth)
                    {
                        double third = (b.X - a.X) / 3;
                        drawing.BezierTo(new Point(a.X + third, a.Y), new Point(b.X - third, b.Y), b, true, false);
                    }
                    else drawing.LineTo(b, true, false);
                }
                var last = Position(points[points.Count - 1]);
                if (last.X < Plot.Right) drawing.LineTo(new Point(Plot.Right, last.Y), true, false);
                if (fill) { drawing.LineTo(new Point(Plot.Right, Plot.Bottom), true, false); drawing.LineTo(new Point(first.X, Plot.Bottom), true, false); }
            }
            geometry.Freeze(); return geometry;
        }

        static void DrawNode(DrawingContext context, Point point, double radius, int shape, Brush fill)
        {
            if (shape == 0) context.DrawEllipse(fill, null, point, radius, radius);
            else if (shape == 2) context.DrawRoundedRectangle(fill, null, new Rect(point.X - radius, point.Y - radius, radius * 2, radius * 2), 1.5, 1.5);
            else
            {
                var diamond = new StreamGeometry();
                using (var drawing = diamond.Open())
                {
                    drawing.BeginFigure(new Point(point.X, point.Y - radius - 1), true, true);
                    drawing.LineTo(new Point(point.X + radius + 1, point.Y), true, false);
                    drawing.LineTo(new Point(point.X, point.Y + radius + 1), true, false);
                    drawing.LineTo(new Point(point.X - radius - 1, point.Y), true, false);
                }
                context.DrawGeometry(fill, null, diamond);
            }
        }
        void DrawFocus(DrawingContext context, Rect outer)
        {
            if (IsKeyboardFocusWithin)
            {
                var focus = outer; focus.Inflate(-3, -3);
                context.DrawRoundedRectangle(null, new Pen(MakeBrush("#E8E1FF"), 2), focus, 15, 15);
            }
        }
        protected override void OnGotKeyboardFocus(KeyboardFocusChangedEventArgs args) { base.OnGotKeyboardFocus(args); InvalidateVisual(); }
        protected override void OnLostKeyboardFocus(KeyboardFocusChangedEventArgs args) { base.OnLostKeyboardFocus(args); InvalidateVisual(); }

        int HitPoint(Point pointer)
        {
            var lane = ActiveLane; if (lane == null) return -1;
            double best = 22 * 22; int found = -1;
            for (int i = 0; i < lane.Points.Count; i++)
            {
                var point = Position(lane.Points[i]);
                double distance = (point - pointer).LengthSquared;
                if (distance <= best) { best = distance; found = i; }
            }
            return found;
        }
        protected override void OnMouseLeftButtonDown(MouseButtonEventArgs args)
        {
            base.OnMouseLeftButtonDown(args); if (!CanEdit) return;
            Focus(); var pointer = args.GetPosition(this); int hit = HitPoint(pointer);
            if (args.ClickCount >= 2 && hit < 0 && Plot.Contains(pointer))
                AddPointAt(TimeAt(pointer.X), LevelAt(pointer.Y));
            else if (hit >= 0)
            {
                SelectedPointIndex = hit;
                if (!Document.IsEditing)
                {
                    Document.BeginEdit(); dragDocument = Document; dragLane = ActiveLaneId; dragIndex = hit; dragging = true;
                    if (!CaptureMouse()) FinishDrag(false);
                }
            }
            else ClearSelection();
            args.Handled = true;
        }
        protected override void OnMouseMove(MouseEventArgs args)
        {
            base.OnMouseMove(args);
            var pointer = args.GetPosition(this);
            Cursor = CanEdit && (dragging || HitPoint(pointer) >= 0) ? Cursors.SizeAll : Cursors.Arrow;
            if (!dragging || args.LeftButton != MouseButtonState.Pressed) return;
            if (!CanEdit || !ReferenceEquals(Document, dragDocument) || dragLane != ActiveLaneId)
            { FinishDrag(false); return; }
            var lane = ActiveLane;
            if (dragIndex >= lane.Points.Count) { FinishDrag(false); return; }
            int minimum = dragIndex == 0 ? 0 : lane.Points[dragIndex - 1].Ms + CurveDocument.MinimumPointSpacingMs;
            int maximum = dragIndex == 0 ? 0 : dragIndex + 1 < lane.Points.Count
                ? lane.Points[dragIndex + 1].Ms - CurveDocument.MinimumPointSpacingMs : Document.LengthMs;
            int ms = Math.Clamp(TimeAt(pointer.X), minimum, maximum);
            TryEdit(() => Document.MovePoint(ActiveLaneId, dragIndex, ms, LevelAt(pointer.Y)));
            args.Handled = true;
        }
        protected override void OnMouseLeftButtonUp(MouseButtonEventArgs args)
        { base.OnMouseLeftButtonUp(args); if (dragging) { FinishDrag(true); args.Handled = true; } }
        protected override void OnLostMouseCapture(MouseEventArgs args)
        { base.OnLostMouseCapture(args); if (dragging && !finishing) FinishDrag(true); }
        void FinishDrag(bool commit)
        {
            if (!dragging || finishing) return;
            finishing = true; dragging = false;
            var document = dragDocument; dragDocument = null;
            try { if (commit) document?.CommitEdit(); else document?.CancelEdit(); }
            finally { if (IsMouseCaptured) ReleaseMouseCapture(); finishing = false; InvalidateVisual(); }
        }

        bool TryEdit(Action edit)
        {
            try { edit(); return true; }
            catch (ArgumentException ex) { EditError?.Invoke(ex.Message); return false; }
            catch (InvalidOperationException ex) { EditError?.Invoke(ex.Message); return false; }
        }
        public bool AddPointAt(int ms, int level)
        {
            if (!CanEdit) return false;
            FinishDrag(true); int index = -1;
            if (!TryEdit(() => index = Document.AddPoint(ActiveLaneId, ms, level))) return false;
            SelectedPointIndex = index; return true;
        }
        public bool DeleteSelectedPoint()
        {
            if (!CanEdit || selected < 0) return false;
            FinishDrag(true); int previous = selected;
            if (!TryEdit(() => Document.DeletePoint(ActiveLaneId, previous))) return false;
            SelectedPointIndex = Math.Min(previous, ActiveLane.Points.Count - 1); return true;
        }
        void AddBetweenPoints()
        {
            var lane = ActiveLane; if (lane == null) return;
            int left = selected;
            if (left < 0 || left >= lane.Points.Count - 1 || lane.Points[left + 1].Ms - lane.Points[left].Ms < 200)
            {
                left = -1; int largest = 199;
                for (int i = 0; i < lane.Points.Count - 1; i++)
                {
                    int gap = lane.Points[i + 1].Ms - lane.Points[i].Ms;
                    if (gap > largest) { largest = gap; left = i; }
                }
            }
            if (left < 0) { EditError?.Invoke("There is no room for another point. Increase the cycle length."); return; }
            int ms = (lane.Points[left].Ms + lane.Points[left + 1].Ms) / 2;
            AddPointAt(ms, Document.LevelAt(ActiveLaneId, ms));
        }

        protected override void OnKeyDown(KeyEventArgs args)
        {
            base.OnKeyDown(args);
            if (args.Key == Key.Escape) { FinishDrag(true); return; } // The app owns global Stop.
            if (!CanEdit) return;
            bool control = (Keyboard.Modifiers & ModifierKeys.Control) != 0, shift = (Keyboard.Modifiers & ModifierKeys.Shift) != 0;
            if (args.Key == Key.Apps || (shift && args.Key == Key.F10))
            {
                var menuPoint = SelectedPoint.HasValue ? Position(SelectedPoint.Value) : new Point(Plot.Left + Plot.Width / 2, Plot.Top + Plot.Height / 2);
                OpenContextMenu(menuPoint); args.Handled = true; return;
            }
            if (control && (args.Key == Key.Z || args.Key == Key.Y))
            {
                FinishDrag(true); TryEdit(() => { if (args.Key == Key.Y || shift) Document.Redo(); else Document.Undo(); });
                args.Handled = true; return;
            }
            var lane = ActiveLane;
            if (args.Key == Key.Home) { SelectedPointIndex = 0; args.Handled = true; return; }
            if (args.Key == Key.End) { SelectedPointIndex = lane.Points.Count - 1; args.Handled = true; return; }
            if (args.Key == Key.PageUp || args.Key == Key.PageDown)
            {
                SelectedPointIndex = Math.Clamp(selected < 0 ? 0 : selected + (args.Key == Key.PageDown ? 1 : -1), 0, lane.Points.Count - 1);
                args.Handled = true; return;
            }
            if (args.Key == Key.Insert) { AddBetweenPoints(); args.Handled = true; return; }
            if (args.Key == Key.Delete || args.Key == Key.Back) { DeleteSelectedPoint(); args.Handled = true; return; }
            if (args.Key != Key.Left && args.Key != Key.Right && args.Key != Key.Up && args.Key != Key.Down) return;
            if (selected < 0) SelectedPointIndex = 0;
            var point = lane.Points[selected];
            int ms = point.Ms, level = point.Level;
            if (args.Key == Key.Up || args.Key == Key.Down)
                level = Math.Clamp(level + (args.Key == Key.Up ? 1 : -1) * (shift ? 5 : 1), 0, 20);
            else if (selected != 0)
            {
                int minimum = lane.Points[selected - 1].Ms + CurveDocument.MinimumPointSpacingMs;
                int maximum = selected + 1 < lane.Points.Count ? lane.Points[selected + 1].Ms - CurveDocument.MinimumPointSpacingMs : Document.LengthMs;
                ms = Math.Clamp(ms + (args.Key == Key.Right ? 1 : -1) * (shift ? 1000 : 100), minimum, maximum);
            }
            TryEdit(() => Document.MovePoint(ActiveLaneId, selected, ms, level));
            args.Handled = true;
        }

        protected override void OnMouseRightButtonUp(MouseButtonEventArgs args)
        {
            base.OnMouseRightButtonUp(args); if (!CanEdit) return;
            OpenContextMenu(args.GetPosition(this)); args.Handled = true;
        }
        void OpenContextMenu(Point pointer)
        {
            FinishDrag(true); Focus();
            int hit = HitPoint(pointer); if (hit >= 0) SelectedPointIndex = hit;
            var menu = new ContextMenu { PlacementTarget = this };
            void Item(string header, bool enabled, Action action)
            { var item = new MenuItem { Header = header, IsEnabled = enabled }; item.Click += (sender, e) => action(); menu.Items.Add(item); }
            Item("Add point here", Plot.Contains(pointer), () => AddPointAt(TimeAt(pointer.X), LevelAt(pointer.Y)));
            Item("Delete point", selected > 0 && ActiveLane.Points.Count > 2, () => DeleteSelectedPoint());
            menu.Items.Add(new Separator());
            Item("Undo", Document.CanUndo, () => Document.Undo());
            Item("Redo", Document.CanRedo, () => Document.Redo());
            menu.PreviewKeyDown += (sender, e) =>
            {
                if (e.Key == Key.Escape) { StopRequested?.Invoke(this, EventArgs.Empty); menu.IsOpen = false; e.Handled = true; }
            };
            menu.IsOpen = true;
        }

        protected override AutomationPeer OnCreateAutomationPeer() => new CurveEditorPeer(this);
        sealed class CurveEditorPeer : FrameworkElementAutomationPeer, IRangeValueProvider
        {
            readonly CurveEditorControl editor;
            public CurveEditorPeer(CurveEditorControl owner) : base(owner) { editor = owner; }
            protected override string GetClassNameCore() => "CurveEditor";
            protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.Custom;
            protected override string GetNameCore()
            {
                var point = editor.SelectedPoint;
                return editor.ActiveLane == null ? "Pattern curve editor, no motor selected"
                    : editor.ActiveLane.DisplayName + (point.HasValue
                        ? ", point " + editor.Seconds(point.Value.Ms) + ", level " + point.Value.Level + " of 20"
                        : ", pattern curve. Select a point.");
            }
            public override object GetPattern(PatternInterface patternInterface) =>
                patternInterface == PatternInterface.RangeValue && editor.SelectedPoint.HasValue ? this : base.GetPattern(patternInterface);
            public bool IsReadOnly => !editor.CanEdit || !editor.SelectedPoint.HasValue;
            public double LargeChange => 5;
            public double SmallChange => 1;
            public double Maximum => 20;
            public double Minimum => 0;
            public double Value => editor.SelectedPoint?.Level ?? 0;
            public void SetValue(double value)
            {
                if (!double.IsFinite(value) || value < 0 || value > 20 || Math.Truncate(value) != value)
                    throw new ArgumentOutOfRangeException(nameof(value));
                editor.Dispatcher.Invoke(() =>
                {
                    if (IsReadOnly) throw new InvalidOperationException("Select an editable point first.");
                    var point = editor.SelectedPoint.Value;
                    editor.Document.MovePoint(editor.ActiveLaneId, editor.SelectedPointIndex, point.Ms, (int)value);
                });
            }
            public void SelectionUpdated(int oldValue, int newValue)
            {
                RaisePropertyChangedEvent(RangeValuePatternIdentifiers.ValueProperty, (double)oldValue, (double)newValue);
                RaiseAutomationEvent(AutomationEvents.AutomationFocusChanged);
            }
        }
    }
}


using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;

namespace LovenseConnector
{
    public sealed class StudioLanePreset
    {
        public Frame[] points { get; set; }
        public string curve { get; set; }="smooth";
        public int level { get; set; }=12;
        public bool selected { get; set; }=true;
        public int group { get; set; }
    }
    public sealed class StudioPreset
    {
        public int version { get; set; }=2;
        public int lengthMs { get; set; }=6000;
        public bool loop { get; set; }=true;
        public int durationMs { get; set; }
        public int transitionMs { get; set; }=100;
        public StudioLanePreset[] lanes { get; set; }
    }
    internal static class StudioPresetRules
    {
        internal static CurveDocument Validate(StudioPreset preset)
        {
            if(preset==null||preset.version!=2||preset.lanes==null||preset.lanes.Length==0||preset.lanes.Length>64)
                throw new ArgumentException("A mix needs 1–64 motor lanes in format version 2.");
            var document=new CurveDocument(preset.lengthMs,preset.loop);
            new PatternSpec { durationMs=preset.durationMs,transitionMs=preset.transitionMs }.Validate();
            foreach(var lane in preset.lanes)
            {
                if(lane==null||lane.points==null||lane.points.Any(point=>point==null)||lane.level<0||lane.level>20||lane.group<0||
                    !Enum.TryParse<CurveInterpolation>(lane.curve,true,out var mode)||!Enum.IsDefined(mode))
                    throw new ArgumentException("The mix contains an invalid motor curve.");
                document.AddLane(document.Lanes.Count.ToString(CultureInfo.InvariantCulture),"Motor",lane.points.Select(point=>new CurvePoint(point.ms,point.level)),mode);
            }
            foreach(var group in Enumerable.Range(0,preset.lanes.Length).GroupBy(index=>preset.lanes[index].group))
            {
                var first=document.Lanes[group.First()];
                if(group.Any(index=>document.Lanes[index].Interpolation!=first.Interpolation||!document.Lanes[index].Points.SequenceEqual(first.Points)))
                    throw new ArgumentException("Linked motor lanes must contain the same curve.");
            }
            return document;
        }

        internal static StudioPreset ConvertLegacy(MixerPreset legacy,string[] motorKeys)
        {
            if(legacy==null||legacy.version!=1||legacy.levels==null||legacy.levels.Count>64||legacy.levels.Keys.Any(string.IsNullOrWhiteSpace)||legacy.levels.Values.Any(level=>level<0||level>20)||
                !new[]{"Steady","Wave","Pulse","Heartbeat","Surge","Staircase"}.Contains(legacy.shape))
                throw new ArgumentException("Invalid older mix.");
            // Keep legacy identity matches in their current slots. Every saved lane
            // without a current slot remains an unassigned draft instead of vanishing.
            var keys=(motorKeys??Array.Empty<string>()).Concat(legacy.levels.Keys).Distinct(StringComparer.Ordinal).ToArray();
            if(keys.Length==0||keys.Length>64||keys.Any(string.IsNullOrWhiteSpace))
                throw new ArgumentException("The older mix needs 1–64 curves including current motor slots.");
            var spec=new PatternSpec { name=legacy.shape,mode=legacy.shape,low=legacy.low,high=legacy.high,periodMs=legacy.periodMs,
                durationMs=legacy.durationMs,transitionMs=legacy.transitionMs };
            if(legacy.shape=="Heartbeat"||legacy.shape=="Surge"||legacy.shape=="Staircase")
            {
                spec.mode="Custom";spec.interpolate=legacy.shape!="Heartbeat";
                double[] times=legacy.shape=="Heartbeat"?new[]{0.0,.14,.28,.42,.58,1.0}:legacy.shape=="Surge"?new[]{0.0,.15,.6,.78,1.0}:new[]{0.0,.25,.5,.75,1.0};
                double[] levels=legacy.shape=="Heartbeat"?new[]{0.0,1.0,0.0,.8,0.0,0.0}:legacy.shape=="Surge"?new[]{0.0,.2,1.0,1.0,0.0}:new[]{0.0,.25,.5,1.0,0.0};
                spec.frames=times.Select((time,index)=>new Frame { ms=(int)(time*spec.periodMs),level=spec.low+(int)Math.Round((spec.high-spec.low)*levels[index]) }).ToArray();
            }
            spec.Validate();
            int segments=Math.Min(100,spec.periodMs/100);
            var savedOrder=legacy.levels.Keys.ToArray();
            return new StudioPreset { lengthMs=spec.periodMs,loop=true,durationMs=legacy.durationMs,transitionMs=legacy.transitionMs,
                lanes=keys.Select((key,index)=>
                {
                    int high=legacy.levels.TryGetValue(key,out int level)?level:legacy.high;
                    double ratio=legacy.high==0?0:high/(double)legacy.high;
                    var motor=ForJoin(spec,0);motor.high=high;motor.low=Math.Min(high,(int)Math.Round(legacy.low*ratio));
                    int savedIndex=Array.IndexOf(savedOrder,key);
                    if(legacy.stagger&&savedIndex>=0)motor.phaseDegrees=savedIndex*360/savedOrder.Length;
                    if(motor.mode=="Custom")
                    {
                        double[] normalized=legacy.shape=="Heartbeat"?new[]{0.0,1.0,0.0,.8,0.0,0.0}:legacy.shape=="Surge"?new[]{0.0,.2,1.0,1.0,0.0}:new[]{0.0,.25,.5,1.0,0.0};
                        for(int n=0;n<motor.frames.Length;n++)motor.frames[n].level=legacy.high==0?(int)Math.Round(high*normalized[n]):(int)Math.Round(motor.frames[n].level*ratio);
                    }
                    return new StudioLanePreset { group=index,selected=legacy.levels.ContainsKey(key),level=high,curve=spec.mode=="Pulse"||(spec.mode=="Custom"&&!spec.interpolate)?"step":"linear",
                        points=Enumerable.Range(0,segments+1).Select(i=>new Frame { ms=i*spec.periodMs/segments,level=motor.At(i*spec.periodMs/(double)segments) }).ToArray() };
                }).ToArray() };
        }

        internal static PatternSpec ForJoin(PatternSpec source,double elapsedMs)
        {
            if(source==null||!double.IsFinite(elapsedMs)||elapsedMs<0)throw new ArgumentException("Invalid playback timeline.");
            double remaining=Math.Min(source.durationMs>0?source.durationMs-elapsedMs:double.PositiveInfinity,
                source.stopAtMs>0?source.stopAtMs-elapsedMs:double.PositiveInfinity);
            if(remaining<=0)return null;
            int duration=double.IsFinite(remaining)?Math.Max(1,(int)Math.Ceiling(remaining)):0;
            return new PatternSpec { version=source.version,name=source.name,mode=source.mode,low=source.low,high=source.high,
                periodMs=source.periodMs,durationMs=duration,dutyPercent=source.dutyPercent,phaseDegrees=source.phaseDegrees,
                transitionMs=source.transitionMs,repeat=source.repeat,interpolate=source.interpolate,curve=source.curve,
                stopAtMs=source.stopAtMs,
                frames=source.frames?.Select(frame=>new Frame { ms=frame.ms,level=frame.level }).ToArray() };
        }
    }
    internal sealed partial class ConnectorApp
    {
        CurveDocument studioDocument;
        CurveEditorControl studioEditor;
        Grid studioRoot,studioSurface,studioPattern,studioGraphGrid;
        ScrollViewer studioLive;
        Border studioInspector,studioDialog;
        WrapPanel studioTargets,studioLaneTabs,studioAudioRoutes;
        ComboBox studioInterpolation,studioPresetChoice;
        TextBox studioTime,studioLevel,studioLength;
        CheckBox studioLoop;
        TextBlock studioEditing,studioPointStatus,studioTargetCount;
        Button studioUndo,studioRedo,studioStart,studioPointButton,studioModeLive,studioModePattern,studioApplyPoint;
        Button studioTargetPicker,studioMore;
        Popup studioOptionsPopup;
        FrameworkElement studioLiveOptions;
        Border studioEmpty;
        bool studioLoading,studioPatternMode,studioRunning,studioRunningPattern,studioPointOpen,studioModeStyled;
        string studioHighlightedLane;
        int studioOperation,studioSession,studioPendingStarts,studioCurveRevision;
        Stopwatch studioClock;
        Stopwatch studioPreviewClock;
        DispatcherTimer studioPreviewTimer;
        Button studioPreview;
        TextBlock studioPreviewLabel;
        int studioDraftSequence;
        readonly Dictionary<string,StudioLanePreset> studioDraftOptions=new Dictionary<string,StudioLanePreset>();
        readonly Dictionary<string,PatternSpec> studioRunSpecs=new Dictionary<string,PatternSpec>();
        readonly Dictionary<string,Tuple<Playback,int>> studioOwned=new Dictionary<string,Tuple<Playback,int>>();
        readonly Dictionary<string,CheckBox> studioTargetChecks=new Dictionary<string,CheckBox>();
        readonly Dictionary<string,Button> studioLaneButtons=new Dictionary<string,Button>();
        readonly Dictionary<string,int> studioTargetVersions=new Dictionary<string,int>();
        bool OwnsStudio => studioRunning&&studioOperation==operatorGeneration&&!closing&&sweep==null&&audioCapture==null&&!feelArmed&&integration==null;

        Border StudioPanel(UIElement child,int padding=18) => new Border { Child=child,CornerRadius=new CornerRadius(23),Padding=new Thickness(padding),
            Background=new LinearGradientBrush((System.Windows.Media.Color)ColorConverter.ConvertFromString("#B34C3A66"),(System.Windows.Media.Color)ColorConverter.ConvertFromString("#B3151022"),125),
            BorderBrush=Color("#24FFFFFF"),BorderThickness=new Thickness(0,1,0,0),Margin=new Thickness(0,0,0,12) };
        static void Accessible(DependencyObject element,string name) => System.Windows.Automation.AutomationProperties.SetName(element,name);
        void InitializeStudioControls()
        {
            studioDocument=new CurveDocument();studioEditor=new CurveEditorControl { Document=studioDocument,MinHeight=150,Margin=new Thickness(0,6,0,4) };
            studioRoot=new Grid();studioRoot.Unloaded+=(s,e)=>StopStudioPreview();studioSurface=new Grid { Margin=new Thickness(22) };studioRoot.Children.Add(studioSurface);
            studioSurface.RowDefinitions.Add(new RowDefinition { Height=GridLength.Auto });
            studioSurface.RowDefinitions.Add(new RowDefinition { Height=new GridLength(1,GridUnitType.Star) });
            studioSurface.RowDefinitions.Add(new RowDefinition { Height=GridLength.Auto });
            var heading=new DockPanel { Margin=new Thickness(0,0,0,14) };
            var modes=new StackPanel { Orientation=Orientation.Horizontal,HorizontalAlignment=HorizontalAlignment.Right };
            studioModeLive=Action("Live",()=>{SetStudioMode(false);QueueStudioAppearance();return Task.CompletedTask;});studioModePattern=Action("Pattern",()=>{SetStudioMode(true);QueueStudioAppearance();return Task.CompletedTask;});
            modes.Children.Add(studioModeLive);modes.Children.Add(studioModePattern);DockPanel.SetDock(modes,Dock.Right);heading.Children.Add(modes);heading.Children.Add(Text("Controls",32));studioSurface.Children.Add(heading);
            mixerBody=new StackPanel();mixerLabel=Text("Find a device to add its motors.",16);mixerBody.Children.Add(mixerLabel);
            mixerMotors=new StackPanel();mixerBody.Children.Add(mixerMotors);
            var liveOptions=new StackPanel();studioLiveOptions=liveOptions;mixerBody.Children.Add(liveOptions);
            var linked=new WrapPanel { Margin=new Thickness(0,12,0,0) };linked.Children.Add(Text("Linked level",16));
            mixerHigh=new Slider { Minimum=0,Maximum=20,Value=12,TickFrequency=1,IsSnapToTickEnabled=true,Width=240,Margin=new Thickness(16,0,12,0) };Accessible(mixerHigh,"Linked motor level");
            var linkedValue=Text("12 / 20",16);linked.Children.Add(mixerHigh);linked.Children.Add(linkedValue);liveOptions.Children.Add(linked);
            linked.Children.Add(Action("Select all",()=>{foreach(var motor in mixerRows.Values.Where(m=>m.Device.Connected))motor.Include.IsChecked=true;return Task.CompletedTask;}));
            mixerHigh.ValueChanged+=(s,e)=>{linkedValue.Text=(int)mixerHigh.Value+" / 20";if(!studioLoading)foreach(var m in SelectedMixer())m.Level.Value=mixerHigh.Value;};
            mixerDuration=Input("0",110);mixerTransition=Input("100",110);
            var timing=new WrapPanel();Field(timing,"Duration (ms, 0 = until stopped)",mixerDuration);Field(timing,"Transition (ms)",mixerTransition);
            liveOptions.Children.Add(new Expander { Header="Live timing",Content=timing,Margin=new Thickness(0,16,0,0) });
            studioLive=new ScrollViewer { Content=mixerBody,VerticalScrollBarVisibility=ScrollBarVisibility.Auto,HorizontalScrollBarVisibility=ScrollBarVisibility.Disabled };Grid.SetRow(studioLive,1);studioSurface.Children.Add(studioLive);

            studioPattern=new Grid();studioPattern.RowDefinitions.Add(new RowDefinition { Height=GridLength.Auto });studioPattern.RowDefinitions.Add(new RowDefinition { Height=GridLength.Auto });studioPattern.RowDefinitions.Add(new RowDefinition { Height=new GridLength(1,GridUnitType.Star) });studioPattern.RowDefinitions.Add(new RowDefinition { Height=GridLength.Auto });
            studioTargets=new WrapPanel { Visibility=Visibility.Collapsed };studioTargetCount=Text("Play targets",14);studioTargets.Children.Add(studioTargetCount);
            Grid.SetRow(studioTargets,0);studioPattern.Children.Add(studioTargets);
            studioLaneTabs=new WrapPanel { Margin=new Thickness(0,4,0,4) };Grid.SetRow(studioLaneTabs,1);studioPattern.Children.Add(studioLaneTabs);
            studioGraphGrid=new Grid();studioGraphGrid.ColumnDefinitions.Add(new ColumnDefinition());studioGraphGrid.ColumnDefinitions.Add(new ColumnDefinition { Width=new GridLength(220) });
            studioGraphGrid.Children.Add(studioEditor);
            studioInspector=BuildStudioInspector();Grid.SetColumn(studioInspector,1);studioGraphGrid.Children.Add(studioInspector);Grid.SetRow(studioGraphGrid,2);studioPattern.Children.Add(studioGraphGrid);
            var patternOptions=new WrapPanel { Margin=new Thickness(0,10,0,0) };
            studioLength=Input("6000",95);Accessible(studioLength,"Pattern length in milliseconds");Field(patternOptions,"Length (ms)",studioLength);
            studioLoop=new CheckBox { Content="Loop",IsChecked=true,VerticalAlignment=VerticalAlignment.Center,Margin=new Thickness(0,18,16,0) };patternOptions.Children.Add(studioLoop);
            studioPresetChoice=new ComboBox { Width=140,Margin=new Thickness(0,0,8,0) };foreach(var name in new[]{"Custom","Wave","Pulse","Heartbeat","Surge","Staircase"})studioPresetChoice.Items.Add(name);studioPresetChoice.SelectedIndex=0;Field(patternOptions,"Preset",studioPresetChoice);
            var patternDuration=Input("0",110);patternDuration.SetBinding(TextBox.TextProperty,new Binding("Text") { Source=mixerDuration,Mode=BindingMode.TwoWay,UpdateSourceTrigger=UpdateSourceTrigger.PropertyChanged });Field(patternOptions,"Duration (ms, 0 = until stopped)",patternDuration);
            patternOptions.Children.Add(Action("Apply length",()=>{studioDocument.SetLength(Number(studioLength));return Task.CompletedTask;}));
            var settingsBody=new StackPanel();settingsBody.Children.Add(Text("Pattern settings",22));settingsBody.Children.Add(patternOptions);
            var settingsCard=StudioPanel(settingsBody,18);settingsCard.Background=Color("#F52C203E");settingsCard.MaxWidth=500;
            studioOptionsPopup=new Popup { Child=settingsCard,AllowsTransparency=true,StaysOpen=false,Placement=PlacementMode.Top };
            settingsBody.PreviewKeyDown+=async(s,e)=>{if(e.Key==Key.Escape){studioOptionsPopup.IsOpen=false;e.Handled=true;await StopAll();}};
            Grid.SetRow(studioPattern,1);studioSurface.Children.Add(studioPattern);
            var emptyBody=new StackPanel();emptyBody.Children.Add(Text("No connected motors",24));emptyBody.Children.Add(Text("Find a device to use the controls.",15));
            emptyBody.Children.Add(Action("Find & connect",async()=>{if(scanning)await EndScan(false);else StartScan(true);}));
            emptyBody.Children.Add(Action("Create a pattern",()=>{SetStudioMode(true);QueueStudioAppearance();return Task.CompletedTask;}));
            studioEmpty=StudioPanel(emptyBody,24);studioEmpty.VerticalAlignment=VerticalAlignment.Top;Grid.SetRow(studioEmpty,1);Panel.SetZIndex(studioEmpty,8);studioSurface.Children.Add(studioEmpty);

            var footer=new WrapPanel { Margin=new Thickness(0,10,0,0) };
            studioStart=Action("Start selected",()=>Manual(StartStudio));studioStart.Background=Color("#886D529D");footer.Children.Add(studioStart);
            studioPreview=Action("Preview",()=>{if(studioPreviewClock!=null)StopStudioPreview();else StartStudioPreview();return Task.CompletedTask;});studioPreview.Width=118;footer.Children.Add(studioPreview);
            studioPreviewLabel=Text("",13);studioPreviewLabel.VerticalAlignment=VerticalAlignment.Center;studioPreviewLabel.Margin=new Thickness(12,0,8,0);studioPreviewLabel.MaxWidth=150;studioPreviewLabel.TextTrimming=TextTrimming.CharacterEllipsis;heading.Children.Add(studioPreviewLabel);
            studioPreviewTimer=new DispatcherTimer { Interval=TimeSpan.FromMilliseconds(33) };studioPreviewTimer.Tick+=(s,e)=>RefreshStudioPreview();
            studioPointButton=Action("Edit point",()=>{studioPointOpen=!studioPointOpen;LayoutStudioInspector();if(studioPointOpen)studioTime.Focus();return Task.CompletedTask;});footer.Children.Add(studioPointButton);
            var more=Action("More…",()=>Task.CompletedTask);studioMore=more;var menu=new ContextMenu();
            MenuItem Menu(string title,Func<Task> action){var item=new MenuItem{Header=title};item.Click+=async(s,e)=>{try{await action();}catch(Exception ex){Report(ex);}};menu.Items.Add(item);return item;}
            Menu("Link curves…",()=>{ShowStudioLink();return Task.CompletedTask;});Menu("Unlink this curve",()=>{if(studioEditor.ActiveLaneId!=null)studioDocument.Unlink(studioEditor.ActiveLaneId);return Task.CompletedTask;});
            Menu("Pattern settings…",()=>{studioOptionsPopup.PlacementTarget=studioMore;studioOptionsPopup.IsOpen=true;studioLength.Focus();return Task.CompletedTask;});
            Menu("Save mix…",SaveStudioMix);Menu("Open mix…",LoadStudioMix);more.ContextMenu=menu;more.Click+=(s,e)=>{menu.PlacementTarget=more;menu.IsOpen=true;};footer.Children.Add(more);
            studioRestoreDraftChoice=new MenuItem { Header="Restore previous draft",IsEnabled=false };studioRestoreDraftChoice.Click+=async(s,e)=>{try{await RestoreStudioDraft();}catch(Exception ex){Report(ex);}};menu.Items.Add(studioRestoreDraftChoice);
            Menu("Add unassigned curve",()=>{AddStudioDraft();SetStudioMode(true);return Task.CompletedTask;});
            Menu("Duplicate curve",()=>{DuplicateStudioCurve();return Task.CompletedTask;});
            var invert=Menu("Invert levels",()=>{studioDocument.InvertLevels(studioEditor.ActiveLaneId);return Task.CompletedTask;});
            menu.Opened+=(s,e)=>invert.IsEnabled=studioDocument.ContainsLane(studioEditor.ActiveLaneId)&&!studioDocument.IsEditing;
            Menu("Assign curve to motor…",()=>{ShowStudioAssignment();return Task.CompletedTask;});
            Menu("Remove unassigned curve",()=>{RemoveStudioDraft();return Task.CompletedTask;});
            menu.PreviewKeyDown+=async(s,e)=>{if(e.Key==Key.Escape){menu.IsOpen=false;e.Handled=true;await StopAll();}};
            studioUndo=Action("Undo",()=>{studioDocument.Undo();return Task.CompletedTask;});studioRedo=Action("Redo",()=>{studioDocument.Redo();return Task.CompletedTask;});footer.Children.Add(studioUndo);footer.Children.Add(studioRedo);
            foreach(var button in footer.Children.OfType<Button>())button.Margin=new Thickness(0,0,6,6);
            Grid.SetRow(footer,2);studioSurface.Children.Add(footer);
            mixerTab=new TabItem { Header="Controls",Content=studioRoot };tabs.Items.Insert(0,mixerTab);tabs.SelectedItem=mixerTab;
            studioEditor.SelectionChanged+=(s,e)=>UpdateStudioInspector();studioEditor.EditError+=message=>hint.Text=message;studioEditor.StopRequested+=async(s,e)=>await StopAll();
            studioDocument.Changed+=async(s,e)=>{StopStudioPreview();studioCurveRevision++;UpdateStudioInspector();if(studioLoading)return;QueueStudioDraftSave(true);studioPresetChoice.SelectedItem="Custom";if(OwnsStudio&&studioRunningPattern)try{await StopStudioMotors();hint.Text="Pattern edited · output stopped. Start selected to play the new curve.";}catch(Exception ex){Report(ex);}};
            studioDocument.HistoryChanged+=(s,e)=>{studioUndo.IsEnabled=studioDocument.CanUndo;studioRedo.IsEnabled=studioDocument.CanRedo;};
            studioPresetChoice.SelectionChanged+=(s,e)=>{if(studioLoading||studioPresetChoice.SelectedIndex<=0||studioEditor.ActiveLaneId==null)return;try{ApplyStudioPreset((string)studioPresetChoice.SelectedItem);}catch(Exception ex){Report(ex);}};
            studioLoop.Checked+=(s,e)=>{if(!studioLoading)studioDocument.SetLoop(true);};studioLoop.Unchecked+=(s,e)=>{if(!studioLoading)studioDocument.SetLoop(false);};
            studioGraphGrid.SizeChanged+=(s,e)=>LayoutStudioInspector();
            window.PreviewKeyDown+=(s,e)=>{if(tabs.SelectedItem!=mixerTab||Keyboard.FocusedElement is TextBox||studioEditor.IsKeyboardFocusWithin)return;if((Keyboard.Modifiers&ModifierKeys.Control)!=0&&(e.Key==Key.Z||e.Key==Key.Y)){e.Handled=true;try{if(e.Key==Key.Y||(Keyboard.Modifiers&ModifierKeys.Shift)!=0)studioDocument.Redo();else studioDocument.Undo();}catch(Exception ex){Report(ex);}}};
            SetStudioMode(false);UpdateStudioInspector();InitializeStudioDraftRecovery();
        }

        Border BuildStudioInspector()
        {
            var panel=new StackPanel();studioEditing=Text("Select a motor",18);panel.Children.Add(studioEditing);studioPointStatus=Text("",13);panel.Children.Add(studioPointStatus);
            studioTime=Input("0",150);studioLevel=Input("0",150);Accessible(studioTime,"Point time in milliseconds");Accessible(studioLevel,"Point vibration level from zero to twenty");
            Field(panel,"Time (ms)",studioTime);Field(panel,"Level / 20",studioLevel);
            studioApplyPoint=Action("Apply point",()=>{if(studioEditor.ActiveLaneId!=null&&studioEditor.SelectedPointIndex>=0)studioDocument.MovePoint(studioEditor.ActiveLaneId,studioEditor.SelectedPointIndex,Number(studioTime),Number(studioLevel));return Task.CompletedTask;});panel.Children.Add(studioApplyPoint);
            studioInterpolation=new ComboBox { Width=150 };foreach(var item in Enum.GetValues<CurveInterpolation>())studioInterpolation.Items.Add(item);Field(panel,"Curve",studioInterpolation);
            studioInterpolation.SelectionChanged+=(s,e)=>{if(!studioLoading&&studioEditor.ActiveLaneId!=null&&studioInterpolation.SelectedItem is CurveInterpolation interpolation)studioDocument.SetInterpolation(studioEditor.ActiveLaneId,interpolation);};
            var points=new WrapPanel();points.Children.Add(Action("Add point",()=>{AddStudioPoint();return Task.CompletedTask;}));points.Children.Add(Action("Delete",()=>{studioEditor.DeleteSelectedPoint();return Task.CompletedTask;}));panel.Children.Add(points);
            panel.Children.Add(Action("Close editor",()=>{studioPointOpen=false;LayoutStudioInspector();studioEditor.Focus();return Task.CompletedTask;}));
            return StudioPanel(new ScrollViewer { Content=panel,VerticalScrollBarVisibility=ScrollBarVisibility.Auto,HorizontalScrollBarVisibility=ScrollBarVisibility.Disabled },16);
        }
        void SetStudioMode(bool pattern)
        {
            if(studioPatternMode!=pattern)StopStudioPreview();
            if(pattern&&studioDocument.Lanes.Count==0)AddStudioDraft();
            if(!studioModeStyled||studioPatternMode!=pattern){studioModeLive.Background=Color(pattern?"#302A213E":"#806C5095");studioModePattern.Background=Color(pattern?"#806C5095":"#302A213E");studioModeStyled=true;}
            studioPatternMode=pattern;bool any=studioDocument?.Lanes.Count>0;studioLive.Visibility=!pattern&&any?Visibility.Visible:Visibility.Collapsed;studioPattern.Visibility=pattern&&any?Visibility.Visible:Visibility.Collapsed;
            studioPointButton.Visibility=pattern&&any?Visibility.Visible:Visibility.Collapsed;studioUndo.Visibility=studioRedo.Visibility=pattern&&any?Visibility.Visible:Visibility.Collapsed;if(studioEmpty!=null)studioEmpty.Visibility=any?Visibility.Collapsed:Visibility.Visible;LayoutStudioInspector();
            if(studioPreview!=null)studioPreview.Visibility=pattern&&any?Visibility.Visible:Visibility.Collapsed;
            if(!pattern&&!mixerRows.Values.Any(m=>m.Device.Connected)){studioLive.Visibility=Visibility.Collapsed;studioEmpty.Visibility=Visibility.Visible;}
        }
        void LayoutStudioInspector()
        {
            if(studioGraphGrid==null)return;bool wide=studioGraphGrid.ActualWidth>=760;
            studioGraphGrid.ColumnDefinitions[1].Width=new GridLength(wide?220:0);Grid.SetColumn(studioInspector,wide?1:0);
            studioInspector.HorizontalAlignment=wide?HorizontalAlignment.Stretch:HorizontalAlignment.Right;studioInspector.Width=wide?double.NaN:230;
            studioInspector.Background=Color(studioUiPreferences?.Material=="Solid"?"#251B3B":wide?"#8A21192F":"#F52C203E");
            studioInspector.Visibility=wide||studioPointOpen?Visibility.Visible:Visibility.Collapsed;Panel.SetZIndex(studioInspector,5);
        }
        void UpdateStudioInspector()
        {
            if(studioDocument==null)return;bool prior=studioLoading;studioLoading=true;
            try {
                if(!studioLength.IsKeyboardFocusWithin)studioLength.Text=studioDocument.LengthMs.ToString(CultureInfo.InvariantCulture);studioLoop.IsChecked=studioDocument.Loop;
                studioUndo.IsEnabled=studioDocument.CanUndo;studioRedo.IsEnabled=studioDocument.CanRedo;
                if(studioHighlightedLane!=studioEditor.ActiveLaneId){foreach(var pair in studioLaneButtons)pair.Value.Background=Color(pair.Key==studioEditor.ActiveLaneId?"#806C5095":"#302A213E");studioHighlightedLane=studioEditor.ActiveLaneId;studioPresetChoice.SelectedItem="Custom";}
                bool hasPoint=studioEditor.SelectedPoint.HasValue;studioTime.IsEnabled=studioLevel.IsEnabled=studioApplyPoint.IsEnabled=hasPoint;
                if(!hasPoint){studioTime.Text="";studioLevel.Text="";}
                if(studioEditor.ActiveLaneId==null||!studioDocument.ContainsLane(studioEditor.ActiveLaneId)){studioEditing.Text="Select a motor";return;}
                var lane=studioDocument.GetLane(studioEditor.ActiveLaneId);studioEditing.Text=lane.DisplayName;studioInterpolation.SelectedItem=lane.Interpolation;
                var count=studioDocument.Lanes.Count(x=>x.GroupId==lane.GroupId);studioPointStatus.Text=!hasPoint?"Select a point to edit.":count>1?"Linked · "+count+" curves":!mixerRows.ContainsKey(lane.Id)?"Unassigned · preview only":"Independent curve";
                int index=studioEditor.SelectedPointIndex;if(index>=0&&index<lane.Points.Count){if(!studioTime.IsKeyboardFocusWithin)studioTime.Text=lane.Points[index].Ms.ToString();if(!studioLevel.IsKeyboardFocusWithin)studioLevel.Text=lane.Points[index].Level.ToString();}
            } finally { studioLoading=prior; }
        }
        void AddStudioPoint()
        {
            if(studioEditor.ActiveLaneId==null)return;var lane=studioDocument.GetLane(studioEditor.ActiveLaneId);
            int best=-1,gap=0;for(int i=1;i<lane.Points.Count;i++)if(lane.Points[i].Ms-lane.Points[i-1].Ms>gap){best=i;gap=lane.Points[i].Ms-lane.Points[i-1].Ms;}
            if(gap<200)throw new ArgumentException("Increase the pattern length to add another point.");
            int at=(lane.Points[best-1].Ms+lane.Points[best].Ms)/2;studioEditor.AddPointAt(at,studioDocument.LevelAt(lane.Id,at));
        }
        void ApplyStudioPreset(string shape)
        {
            int length=studioDocument.LengthMs;double[] times,levels;CurveInterpolation mode=CurveInterpolation.Smooth;
            switch(shape){case "Pulse":times=new[]{0.0,.25,.5,.75,1.0};levels=new[]{0.0,1.0,0.0,1.0,0.0};mode=CurveInterpolation.Step;break;
                case "Heartbeat":times=new[]{0.0,.14,.28,.42,.58,1.0};levels=new[]{0.0,1.0,0.0,.8,0.0,0.0};break;
                case "Surge":times=new[]{0.0,.2,.6,.8,1.0};levels=new[]{0.0,.2,1.0,1.0,0.0};break;
                case "Staircase":times=new[]{0.0,.25,.5,.75,1.0};levels=new[]{0.0,.25,.5,1.0,0.0};mode=CurveInterpolation.Step;break;
                default:times=new[]{0.0,.25,.5,.75,1.0};levels=new[]{0.0,.5,1.0,.5,0.0};break;}
            studioDocument.ReplaceCurve(studioEditor.ActiveLaneId,times.Select((t,i)=>new CurvePoint((int)Math.Round(t*length),(int)Math.Round(levels[i]*20))),mode);
            bool wasLoading=studioLoading;studioLoading=true;try{studioPresetChoice.SelectedItem=shape;}finally{studioLoading=wasLoading;}
        }

        void RefreshStudioMotors()
        {
            if(mixerMotors==null)return;bool added=false;bool priorLoading=studioLoading;studioLoading=true;
            try {
                foreach(var d in devices.Values)foreach(int channel in d.Motors.Keys){string key=MixerKey(d,channel);if(mixerRows.ContainsKey(key))continue;
                    if(studioDocument.IsEditing)continue; // Discovery must not split a point-drag transaction.
                    studioDocument.AddLane(key,d.Name+(channel==0?"":" · Motor "+channel));
                    var m=new MixerMotor { Device=d,Channel=channel };var panel=new Grid();panel.ColumnDefinitions.Add(new ColumnDefinition { Width=new GridLength(150) });panel.ColumnDefinitions.Add(new ColumnDefinition());panel.ColumnDefinitions.Add(new ColumnDefinition { Width=new GridLength(64) });
                    var identity=new StackPanel();identity.Children.Add(new TextBlock { Text=d.Name,TextTrimming=TextTrimming.CharacterEllipsis,MaxWidth=118,FontSize=15 });identity.Children.Add(new TextBlock { Text=channel==0?"Motor":"Motor "+channel,Foreground=Color("#C4BAD7"),FontSize=13,Margin=new Thickness(0,2,0,0) });
                    var labels=new StackPanel();m.Include=new CheckBox { Content=identity,IsChecked=!studioRunning&&audioCapture==null&&!feelArmed&&integration==null,Margin=new Thickness(0,0,0,8) };Accessible(m.Include,d.Name+" motor "+channel+" enabled");labels.Children.Add(m.Include);
                    var value=Text("12 / 20",19);labels.Children.Add(value);m.Status=Text("Sent: unknown",12);labels.Children.Add(m.Status);panel.Children.Add(labels);
                    m.Trace=new LiveMotorTrace { VerticalAlignment=VerticalAlignment.Center,Margin=new Thickness(6,0,16,0) };Grid.SetColumn(m.Trace,1);panel.Children.Add(m.Trace);
                    m.Level=new Slider { Orientation=Orientation.Vertical,Height=100,Minimum=0,Maximum=20,Value=12,TickFrequency=1,IsSnapToTickEnabled=true,SmallChange=1,LargeChange=1,VerticalAlignment=VerticalAlignment.Center };Accessible(m.Level,d.Name+" motor "+channel+" live level");Grid.SetColumn(m.Level,2);panel.Children.Add(m.Level);
                    m.Meter=new ProgressBar { Minimum=0,Maximum=20,Visibility=Visibility.Collapsed };m.Card=StudioPanel(panel,16);m.Card.Margin=new Thickness(0,0,0,12);mixerRows.Add(key,m);mixerMotors.Children.Add(m.Card);
                    m.Level.ValueChanged+=(s,e)=>{value.Text=(int)m.Level.Value+" / 20";QueueStudioDraftSave();if(!studioLoading&&OwnsStudio&&!studioRunningPattern&&m.Include.IsChecked==true)m.Device.Player?.LiveLevel(m.Channel,(int)m.Level.Value);};
                    m.Include.Checked+=async(s,e)=>await StudioTargetChanged(m);m.Include.Unchecked+=async(s,e)=>await StudioTargetChanged(m);
                    added=true;
                }
                foreach(var pair in mixerRows){var m=pair.Value;bool available=m.Device.Connected&&m.Device.Player!=null;var lane=studioDocument.GetLane(pair.Key);if(lane.IsAvailable&&!available)m.Include.IsChecked=false;if(studioOwned.TryGetValue(pair.Key,out var owned)&&(!available||!ReferenceEquals(owned.Item1,m.Device.Player)))studioOwned.Remove(pair.Key);m.Card.IsEnabled=available&&sweep==null&&!closing;studioDocument.SetLaneAvailable(pair.Key,available);studioDocument.RenameLane(pair.Key,m.Device.Name+(m.Channel==0?"":" · Motor "+m.Channel));}
            } finally {studioLoading=priorLoading;}
            if(added){RebuildStudioSelectors();if(studioEditor.ActiveLaneId==null&&studioDocument.Lanes.Count>0)studioEditor.ActiveLaneId=studioDocument.Lanes[0].Id;}
            mixerLabel.Visibility=mixerRows.Values.Any(m=>m.Device.Connected)?Visibility.Collapsed:Visibility.Visible;studioStart.IsEnabled=SelectedMixer().Length>0;studioLiveOptions.Visibility=mixerRows.Values.Any(m=>m.Device.Connected)?Visibility.Visible:Visibility.Collapsed;
            if(audioCapture!=null)audioTargets=mixerRows.Values.Where(m=>m.Device.Connected).ToArray();SetStudioMode(studioPatternMode);studioEditor.Refresh();UpdateStudioInspector();UpdateStudioTargetCount();
            if(added)QueueStudioAppearance();
        }
        void RebuildStudioSelectors()
        {
            studioTargets.Children.Clear();studioTargets.Children.Add(studioTargetCount);studioLaneTabs.Children.Clear();studioAudioRoutes?.Children.Clear();studioTargetChecks.Clear();studioLaneButtons.Clear();studioHighlightedLane=null;
            var targetsMenu=new ContextMenu();
            int index=0;foreach(var pair in mixerRows){var m=pair.Value;string key=pair.Key;int slot=++index;
                var choice=new CheckBox { Content="Motor "+slot,ToolTip=m.Device.Name+" · "+m.Channel,Margin=new Thickness(12,8,4,8),VerticalAlignment=VerticalAlignment.Center };
                choice.SetBinding(ToggleButton.IsCheckedProperty,new Binding("IsChecked") { Source=m.Include,Mode=BindingMode.TwoWay });studioTargets.Children.Add(choice);studioTargetChecks[key]=choice;
                choice.SetBinding(UIElement.IsEnabledProperty,new Binding("IsEnabled") { Source=m.Card });
                var targetItem=new MenuItem { Header="Motor "+slot,ToolTip=m.Device.Name+" · "+m.Channel,IsCheckable=true,StaysOpenOnClick=true };
                targetItem.SetBinding(MenuItem.IsCheckedProperty,new Binding("IsChecked") { Source=m.Include,Mode=BindingMode.TwoWay });targetItem.SetBinding(UIElement.IsEnabledProperty,new Binding("IsEnabled") { Source=m.Card });targetsMenu.Items.Add(targetItem);
                if(studioAudioRoutes!=null){var p=new StackPanel();var route=new CheckBox { Content=m.Device.Name+" · Motor "+m.Channel };route.SetBinding(ToggleButton.IsCheckedProperty,new Binding("IsChecked") { Source=m.Include,Mode=BindingMode.TwoWay });p.Children.Add(route);var gain=new Slider{Minimum=0,Maximum=20,Width=180,TickFrequency=1,IsSnapToTickEnabled=true,Margin=new Thickness(0,14,0,0)};gain.SetBinding(RangeBase.ValueProperty,new Binding("Value"){Source=m.Level,Mode=BindingMode.TwoWay});Accessible(gain,m.Device.Name+" sound maximum level");p.Children.Add(gain);var card=StudioPanel(p);card.Margin=new Thickness(0,0,12,12);studioAudioRoutes.Children.Add(card);}
            }
            int draftIndex=0;foreach(var lane in studioDocument.Lanes){string key=lane.Id;string title=mixerRows.ContainsKey(key)?"Motor "+(mixerRows.Keys.ToList().IndexOf(key)+1):"Draft "+(++draftIndex);var laneButton=Action(title,()=>{studioEditor.ActiveLaneId=key;UpdateStudioInspector();QueueStudioAppearance();return Task.CompletedTask;});laneButton.ToolTip=lane.DisplayName;studioLaneTabs.Children.Add(laneButton);studioLaneButtons[key]=laneButton;}
            targetsMenu.Items.Add(new Separator());var all=new MenuItem { Header="Select all" };all.Click+=(s,e)=>{foreach(var m in mixerRows.Values.Where(m=>m.Device.Connected))m.Include.IsChecked=true;};targetsMenu.Items.Add(all);
            var clear=new MenuItem { Header="Clear" };clear.Click+=(s,e)=>{foreach(var m in mixerRows.Values)m.Include.IsChecked=false;};targetsMenu.Items.Add(clear);
            studioTargetPicker=Action("Play targets",()=>{targetsMenu.PlacementTarget=studioTargetPicker;targetsMenu.IsOpen=true;return Task.CompletedTask;});studioTargetPicker.ContextMenu=targetsMenu;studioLaneTabs.Children.Add(studioTargetPicker);
            targetsMenu.PreviewKeyDown+=async(s,e)=>{if(e.Key==Key.Escape){targetsMenu.IsOpen=false;e.Handled=true;await StopAll();}};
            UpdateStudioTargetCount();
        }
        void UpdateStudioTargetCount(){int count=SelectedMixer().Length;if(studioTargetPicker!=null)studioTargetPicker.Content="Play targets · "+count;studioTargetCount.Text="Play targets · "+count;}

        async Task StudioTargetChanged(MixerMotor motor)
        {
            if(studioLoading)return;studioStart.IsEnabled=SelectedMixer().Length>0;UpdateStudioTargetCount();
            QueueStudioDraftSave();
            string key=MixerKey(motor.Device,motor.Channel);int session=studioSession,operation=operatorGeneration;
            int targetVersion=studioTargetVersions.GetValueOrDefault(key)+1;studioTargetVersions[key]=targetVersion;
            var player=motor.Device.Player;
            try {
                if(OwnsStudio&&player!=null)
                {
                    if(motor.Include.IsChecked!=true)
                    {
                        studioOwned.Remove(key);
                        if(!SelectedMixer().Any())ClearStudioOwnership();
                        await player.Stop(motor.Channel);
                    }
                    else if(motor.Device.Connected)await JoinStudioMotor(motor,session,operation);
                }
                else if(audioCapture!=null&&motor.Include.IsChecked!=true&&player!=null)await player.Stop(motor.Channel);
            }
            catch(Exception ex)
            {
                Report(ex);
                if(session==studioSession&&operation==operatorGeneration&&studioTargetVersions.GetValueOrDefault(key)==targetVersion&&OwnsStudio)
                    try{await StopStudioMotors();}catch(Exception stopError){Report(stopError);}
            }
        }
        PatternSpec StudioLiveSpec(MixerMotor m) => new PatternSpec { mode="Steady",high=(int)m.Level.Value,durationMs=Number(mixerDuration),stopAtMs=Number(mixerDuration),transitionMs=Number(mixerTransition) };
        PatternSpec StudioCurveSpec(string key)
        {
            int requested=Number(mixerDuration);new PatternSpec { durationMs=requested }.Validate();
            int duration=studioDocument.Loop?requested:requested==0?studioDocument.LengthMs:Math.Min(requested,studioDocument.LengthMs);
            var lane=studioDocument.GetLane(key);return new PatternSpec { name="Custom",mode="Custom",high=20,periodMs=studioDocument.LengthMs,repeat=studioDocument.Loop,durationMs=duration,stopAtMs=duration,transitionMs=0,curve=lane.Interpolation.ToString().ToLowerInvariant(),frames=lane.Points.Select(p=>new Frame{ms=p.Ms,level=p.Level}).ToArray() };
        }
        async Task StartStudio()
        {
            StopStudioPreview();
            var targets=SelectedMixer();if(targets.Length==0)throw new ArgumentException("Connect and select at least one motor.");
            bool pattern=studioPatternMode;int operation=operatorGeneration,revision=studioCurveRevision;
            var players=targets.ToDictionary(m=>MixerKey(m.Device,m.Channel),m=>m.Device.Player);
            var specs=mixerRows.Where(p=>p.Value.Device.Connected).ToDictionary(p=>p.Key,p=>pattern?StudioCurveSpec(p.Key):StudioLiveSpec(p.Value));foreach(var spec in specs.Values)spec.Validate();
            ClearStudioOwnership();if(!await StopPlayers())throw new InvalidOperationException("Output could not be stopped before starting.");if(operation!=operatorGeneration||closing)return;
            if(pattern&&revision!=studioCurveRevision){hint.Text="The curve changed while starting. Start again to play the edited curve.";return;}
            studioRunning=true;studioRunningPattern=pattern;studioOperation=operation;studioClock=Stopwatch.StartNew();studioRunSpecs.Clear();foreach(var pair in specs)studioRunSpecs[pair.Key]=pair.Value;
            int session=studioSession;studioPendingStarts++;
            try{foreach(var m in targets){if(!OwnsStudio||session!=studioSession)return;string key=MixerKey(m.Device,m.Channel);if(m.Include.IsChecked==true&&m.Device.Connected&&ReferenceEquals(m.Device.Player,players[key]))await JoinStudioMotor(m,session,operation);}}
            catch{if(session==studioSession)await StopStudioMotors();throw;}
            finally{if(session==studioSession)studioPendingStarts--;}
            RefreshStudioPlaybackState();RefreshStudioDock();
        }
        async Task JoinStudioMotor(MixerMotor motor,int session,int operation)
        {
            if(!OwnsStudio||session!=studioSession||operation!=operatorGeneration||studioClock==null)return;
            string key=MixerKey(motor.Device,motor.Channel);var player=motor.Device.Player;var clock=studioClock;
            if(player==null||!motor.Device.Connected||motor.Include.IsChecked!=true)return;
            var source=studioRunSpecs.GetValueOrDefault(key);
            if(source==null&&!studioRunningPattern)source=studioRunSpecs.Values.FirstOrDefault();
            if(source!=null&&!studioRunningPattern){source=StudioPresetRules.ForJoin(source,0);if(source!=null){source.high=(int)motor.Level.Value;source.low=0;}}
            if(source==null){hint.Text="Restart the pattern to include this motor.";return;}
            var spec=StudioPresetRules.ForJoin(source,clock.Elapsed.TotalMilliseconds);
            if(spec==null){hint.Text="The pattern has finished. Start again to replay it.";return;}
            spec.Validate();studioOwned[key]=Tuple.Create(player,motor.Channel);studioPendingStarts++;
            try{await player.Start(motor.Channel,spec,20,clock);}
            finally{if(session==studioSession)studioPendingStarts--;}
        }
        void ClearStudioOwnership(){studioRunning=false;studioSession++;studioPendingStarts=0;studioClock=null;studioRunSpecs?.Clear();studioOwned?.Clear();studioTargetVersions?.Clear();}
        async Task StopStudioMotors()
        {
            var owned=studioOwned.ToArray();ClearStudioOwnership();await Task.WhenAll(owned.Select(p=>p.Value.Item1.Stop(p.Value.Item2)));RefreshStudioDock();
        }
        void RefreshStudioPlaybackState()
        {
            if(!studioRunning)return;if(!OwnsStudio){ClearStudioOwnership();return;}
            if(studioPendingStarts==0&&(studioOwned.Count==0||!studioOwned.Values.Any(p=>p.Item1.Active(p.Item2))))ClearStudioOwnership();
        }
        void RefreshStudioDock()
        {
            if(window==null)return;var source=Find<TextBlock>("OutputSource");var sent=Find<TextBlock>("OutputLevels");if(source==null||sent==null)return;
            var motors=devices.Values.Where(d=>d.Connected&&d.Player!=null).SelectMany(d=>d.Channels.Select(c=>d.Player.Level(c))).ToArray();sent.Text=motors.Length==0?"No motors connected":"Sent: "+string.Join(" · ",motors.Select((n,i)=>"M"+(i+1)+": "+(n<0?"Unknown":n.ToString())));
            source.Text=sweep!=null?"Device test":screenArmed?"Screen sync":feelArmed?"Video sync":audioCapture!=null?"Sound sync":integration!=null?integration.Name:OwnsStudio?(studioRunningPattern?"Pattern playing":"Live controls"):devices.Values.Any(d=>d.Player?.Running==true)?"Device controls":motors.Any(level=>level<0)?"Output unconfirmed":"Output off";
        }

        void ShowStudioLink()
        {
            if(studioDocument.Lanes.Count<2||studioEditor.ActiveLaneId==null){hint.Text="Add at least two curves to link them.";return;}
            if(studioDialog!=null){studioRoot.Children.Remove(studioDialog);studioDialog=null;}
            var panel=new StackPanel();panel.Children.Add(Text("Link curves",26));var source=new ComboBox { ItemsSource=studioDocument.Lanes,DisplayMemberPath="DisplayName",SelectedValuePath="Id",SelectedValue=studioEditor.ActiveLaneId,Margin=new Thickness(0,12,0,12) };panel.Children.Add(source);
            var choices=new Dictionary<string,CheckBox>();foreach(var lane in studioDocument.Lanes){var check=new CheckBox { Content=lane.DisplayName,IsChecked=lane.Id==studioEditor.ActiveLaneId,Margin=new Thickness(0,8,0,8) };choices[lane.Id]=check;panel.Children.Add(check);}
            var consequence=Text("",14);panel.Children.Add(consequence);panel.Children.Add(Text("Undo restores the previous curves.",13));var actions=new WrapPanel();
            void Close(){studioSurface.IsEnabled=true;studioRoot.Children.Remove(studioDialog);studioDialog=null;studioEditor.Focus();}
            actions.Children.Add(Action("Cancel",()=>{Close();return Task.CompletedTask;}));var apply=Action("Link selected",()=>{studioDocument.LinkCurves((string)source.SelectedValue,choices.Where(p=>p.Value.IsChecked==true).Select(p=>p.Key));Close();return Task.CompletedTask;});actions.Children.Add(apply);panel.Children.Add(actions);panel.Children.Add(Action("Stop all · Esc",async()=>await StopAll()));
            bool updating=false;
            void Describe()
            {
                if(updating)return;updating=true;
                try
                {
                    string id=(string)source.SelectedValue;
                    foreach(var pair in choices){pair.Value.IsEnabled=pair.Key!=id;if(pair.Key==id)pair.Value.IsChecked=true;}
                    var members=choices.Where(pair=>pair.Value.IsChecked==true).Select(pair=>studioDocument.GetLane(pair.Key).DisplayName).ToArray();
                    apply.IsEnabled=id!=null&&members.Length>1;
                    consequence.Text=id==null?"Choose a source curve.":members.Length<2?"Choose another curve to link.":"Use "+studioDocument.GetLane(id).DisplayName+"'s curve for "+string.Join(" + ",members)+". The other selected curves will be replaced.";
                }
                finally{updating=false;}
            }
            source.SelectionChanged+=(s,e)=>Describe();foreach(var choice in choices.Values){choice.Checked+=(s,e)=>Describe();choice.Unchecked+=(s,e)=>Describe();}Describe();
            var card=StudioPanel(new ScrollViewer{Content=panel,VerticalScrollBarVisibility=ScrollBarVisibility.Auto},24);card.Width=400;card.MaxHeight=Math.Max(250,studioRoot.ActualHeight-24);card.VerticalAlignment=VerticalAlignment.Center;card.HorizontalAlignment=HorizontalAlignment.Center;card.Background=Color("#F52C203E");
            KeyboardNavigation.SetTabNavigation(card,KeyboardNavigationMode.Cycle);
            studioDialog=new Border { Background=Color("#B0060410"),Child=card,CornerRadius=new CornerRadius(24) };Panel.SetZIndex(studioDialog,20);studioSurface.IsEnabled=false;studioRoot.Children.Add(studioDialog);source.Focus();
            QueueStudioAppearance();
        }

        async Task SaveStudioMix()
        {
            if(!await StopAll())return;var preset=ExportStudioMix();
            var dialog=new SaveFileDialog { Filter="HapticBridge mixes|*.mix.json",FileName="My mix.mix.json" };if(dialog.ShowDialog(window)==true)File.WriteAllText(dialog.FileName,json.Serialize(preset));
        }
        string[] StudioMotorOrder()=>mixerRows.OrderByDescending(pair=>pair.Value.Device.Connected&&pair.Value.Device.Player!=null).Select(pair=>pair.Key).ToArray();
        async Task LoadStudioMix()
        {
            if(!await StopAll())return;var dialog=new OpenFileDialog { Filter="HapticBridge mixes|*.mix.json;*.json" };if(dialog.ShowDialog(window)!=true)return;
            if(new FileInfo(dialog.FileName).Length>1048576)throw new ArgumentException("Mix file is too large.");string text=File.ReadAllText(dialog.FileName);var preset=json.Deserialize<StudioPreset>(text);
            if(preset?.version==1)preset=StudioPresetRules.ConvertLegacy(json.Deserialize<MixerPreset>(text),StudioMotorOrder());
            ApplyStudioMix(preset);
        }
        void ApplyStudioMix(StudioPreset preset)
        {
            var validated=StudioPresetRules.Validate(preset);var keys=StudioMotorOrder();
            var replacement=new CurveDocument(preset.lengthMs,preset.loop);
            var draftOptions=new Dictionary<string,StudioLanePreset>();var importedKeys=new List<string>();
            for(int i=0;i<Math.Max(keys.Length,preset.lanes.Length);i++)
            {
                string key=i<keys.Length?keys[i]:NewStudioDraftKey();
                string name=i<keys.Length?studioDocument.GetLane(key).DisplayName:"Unassigned curve "+(i+1);
                var lane=i<preset.lanes.Length?validated.Lanes[i]:null;
                replacement.AddLane(key,name,lane?.Points,lane?.Interpolation??CurveInterpolation.Smooth);
                if(i<keys.Length)replacement.SetLaneAvailable(key,mixerRows[key].Device.Connected&&mixerRows[key].Device.Player!=null);
                else draftOptions[key]=new StudioLanePreset { level=preset.lanes[i].level,selected=preset.lanes[i].selected };
                if(i<preset.lanes.Length)importedKeys.Add(key);
            }
            foreach(var group in Enumerable.Range(0,preset.lanes.Length).GroupBy(i=>preset.lanes[i].group))
            {var ids=group.Select(i=>importedKeys[i]).ToArray();if(ids.Length>1)replacement.LinkCurves(ids[0],ids);}
            replacement.ClearHistory();var previousDocument=studioDocument.Copy();var previousDrafts=studioDraftOptions.ToArray();
            var previousLevels=keys.ToDictionary(key=>key,key=>mixerRows[key].Level.Value);
            var previousTargets=keys.ToDictionary(key=>key,key=>mixerRows[key].Include.IsChecked);
            string previousDuration=mixerDuration.Text,previousTransition=mixerTransition.Text;bool wasLoading=studioLoading;
            StopStudioPreview();studioLoading=true;
            try
            {
                for(int i=0;i<keys.Length;i++)
                {
                    var motor=mixerRows[keys[i]];
                    if(i<preset.lanes.Length){var lane=preset.lanes[i];motor.Level.Value=lane.level;motor.Include.IsChecked=lane.selected&&motor.Device.Connected;}
                    else {motor.Level.Value=0;motor.Include.IsChecked=false;}
                }
                mixerDuration.Text=preset.durationMs.ToString(CultureInfo.InvariantCulture);mixerTransition.Text=preset.transitionMs.ToString(CultureInfo.InvariantCulture);
                studioDraftOptions.Clear();foreach(var pair in draftOptions)studioDraftOptions.Add(pair.Key,pair.Value);
                studioDocument.ReplaceWith(replacement);studioEditor.ClearSelection();
            }
            catch
            {
                foreach(var key in keys){mixerRows[key].Level.Value=previousLevels[key];mixerRows[key].Include.IsChecked=previousTargets[key];}
                mixerDuration.Text=previousDuration;mixerTransition.Text=previousTransition;
                studioDraftOptions.Clear();foreach(var pair in previousDrafts)studioDraftOptions.Add(pair.Key,pair.Value);
                studioDocument.ReplaceWith(previousDocument);throw;
            }
            finally{studioLoading=wasLoading;}
            RebuildStudioSelectors();studioPresetChoice.SelectedItem="Custom";SetStudioMode(true);UpdateStudioInspector();UpdateStudioTargetCount();studioStart.IsEnabled=SelectedMixer().Length>0;
            hint.Text=draftOptions.Count>0?"Mix loaded · "+draftOptions.Count+" unassigned curves preserved · output off":"Mix loaded · output off";
            QueueStudioDraftSave(true);
        }
    }
}

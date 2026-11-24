using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Threading;

namespace LovenseConnector
{
    internal sealed partial class ConnectorApp
    {
        sealed class ScreenRoute { public Device Device;public int Channel;public CheckBox Include;public Slider Ceiling;public readonly Dictionary<string,CheckBox> Events=new Dictionary<string,CheckBox>(); }
        sealed class ScreenTarget { public Device Device;public int Channel,Generation,Ceiling,Events,Priority,CommandFailed;public double LastCue=-10;public Playback Player; }
        sealed class ScreenSample { public IScreenCapture Source;public ScreenPixels Pixels; }
        sealed class ScreenStopAttempt
        {
            public ScreenTarget Target;public PlaybackStopRevision Revision;public bool Pending;
            public bool Current=>ReferenceEquals(Target.Device.Player,Target.Player)&&Target.Player.HasUnconfirmedStop(Target.Channel,Revision);
        }
        IScreenCapture screenCapture;
        ScreenSample screenPending;
        ScreenPixels screenFrame;
        ScreenProfile screenProfile=new ScreenProfile();
        HudGlyph[] screenGlyphs;
        readonly HudEventFilter screenFilter=new HudEventFilter();
        readonly HudSpectatorFilter screenSpectatorFilter=new HudSpectatorFilter();
        readonly HudDeathMarkerFilter screenDeathMarkerFilter=new HudDeathMarkerFilter();
        readonly Dictionary<string,ScreenRoute> screenRoutes=new Dictionary<string,ScreenRoute>();
        readonly List<ScreenTarget> screenOwned=new List<ScreenTarget>();
        readonly List<ScreenStopAttempt> screenStopRetries=new List<ScreenStopAttempt>();
        readonly Queue<string> screenEvents=new Queue<string>();
        readonly List<ScreenProfile> screenCalibrationUndo=new List<ScreenProfile>();
        HudPreview screenPreview;
        ComboBox screenWindows,screenRegion;
        TextBlock screenStatus,screenReading,screenHistory,screenNoMotors;
        StackPanel screenMotorPanel;
        Button screenFreeze,screenOutput,screenUndoCalibration;
        Action screenRefreshCoordinates;
        TextBox screenHealthText;
        CheckBox screenReadShield,screenReadKillFeed;
        bool screenCalibrationRefreshing;
        DispatcherTimer screenTimer;
        bool screenFrozen,screenArmed,screenTickBusy,screenCueBusy,screenWaiting;
        int screenGeneration,screenOperation,screenCaptureRequest,screenResumeFrames;
        double screenLastGood=-1,screenDeathUntil;
        double screenNextStopRetry;
        string screenFailure;
        string ScreenProfilePath=>Path.Combine(dataDir??root,"screen-profile.json");
        void InitializeScreenSync()
        {
            try { if(File.Exists(ScreenProfilePath)&&new FileInfo(ScreenProfilePath).Length<=150000) { var saved=json.Deserialize<ScreenProfile>(File.ReadAllText(ScreenProfilePath));if(saved!=null&&saved.Validate())screenProfile=saved; } }catch{}
            LoadScreenFeedback();
            screenGlyphs=HudDefaultGlyphs.Create();
            var body=new StackPanel { Margin=new Thickness(24),MaxWidth=1000 };body.Children.Add(Text("Screen sync",32));
            body.Children.Add(Text("VALORANT · experimental HUD detection",14));
            var source=new StackPanel();source.Children.Add(Text("Window",16));var sourceRow=new Grid();sourceRow.ColumnDefinitions.Add(new ColumnDefinition());sourceRow.ColumnDefinitions.Add(new ColumnDefinition { Width=GridLength.Auto });
            screenWindows=new ComboBox { MinWidth=150,HorizontalAlignment=HorizontalAlignment.Stretch,Margin=new Thickness(0,0,10,8) };Accessible(screenWindows,"Window to capture");sourceRow.Children.Add(screenWindows);
            screenWindows.SelectionChanged+=async(s,e)=>await StopScreenCapture();
            var refresh=Action("Refresh",()=>{RefreshScreenWindows();return Task.CompletedTask;});Grid.SetColumn(refresh,1);sourceRow.Children.Add(refresh);source.Children.Add(sourceRow);
            var captureButtons=new WrapPanel();captureButtons.Children.Add(Action("Start preview",StartScreenPreview));captureButtons.Children.Add(Action("Stop capture",StopScreenCapture));
            screenFreeze=Action("Freeze",ToggleScreenFreeze);captureButtons.Children.Add(screenFreeze);source.Children.Add(captureButtons);
            screenStatus=Text("Capture off · output off",14);source.Children.Add(screenStatus);body.Children.Add(StudioPanel(source));
            screenPreview=new HudPreview { Profile=screenProfile,Margin=new Thickness(0,12,0,4) };Accessible(screenPreview,"HUD preview. Freeze, choose Health, Shield, Kill badge, Spectator marker or Death text, and drag a region. Numeric coordinates are also available below.");body.Children.Add(screenPreview);
            screenReading=Text("Health —    Badge —",14);body.Children.Add(screenReading);
            var calibration=new StackPanel();
            var selection=new WrapPanel();screenRegion=new ComboBox { Width=140,Margin=new Thickness(0,0,10,8) };screenRegion.Items.Add("Health");screenRegion.Items.Add("Kill badge");screenRegion.Items.Add("Shield");screenRegion.Items.Add("Spectator marker");screenRegion.Items.Add("Death text");screenRegion.SelectedIndex=0;Accessible(screenRegion,"HUD element to select");selection.Children.Add(screenRegion);var regionHelp=Text("Freeze, then select health digits only. Leave room for 100.",13);selection.Children.Add(regionHelp);calibration.Children.Add(selection);
            screenReadShield=new CheckBox { Content="Read shield damage",IsChecked=screenProfile.ReadShield,Margin=new Thickness(0,0,0,10) };Accessible(screenReadShield,"Read shield damage from the calibrated shield number");calibration.Children.Add(screenReadShield);
            screenReadShield.Checked+=async(s,e)=>{if(!screenCalibrationRefreshing)await SetScreenShieldEnabled(true);};screenReadShield.Unchecked+=async(s,e)=>{if(!screenCalibrationRefreshing)await SetScreenShieldEnabled(false);};
            screenReadKillFeed=new CheckBox {Content="Use kill feed instead of badges",IsChecked=screenProfile.ReadKillFeed,Margin=new Thickness(0,0,0,10)};Accessible(screenReadKillFeed,"Detect personal kills from the top-right feed with gold killer outlines");calibration.Children.Add(screenReadKillFeed);
            screenReadKillFeed.Checked+=async(s,e)=>{if(!screenCalibrationRefreshing)await SetScreenKillFeed(true);};screenReadKillFeed.Unchecked+=async(s,e)=>{if(!screenCalibrationRefreshing)await SetScreenKillFeed(false);};
            screenPreview.Selected+=SetScreenRegion;
            var learning=new WrapPanel();screenHealthText=Input("100",75);Accessible(screenHealthText,"Selected health or shield number visible in frozen preview");learning.Children.Add(screenHealthText);
            var learnActions=new[]{Action("Learn digits",LearnScreenDigits),Action("Learn kill badge",LearnScreenBadge),Action("Learn spectator marker",LearnScreenSpectator),Action("Learn death text",LearnScreenDeathMarker)};
            foreach(var action in learnActions)learning.Children.Add(action);
            void RefreshLearning() {
                int kind=screenRegion.SelectedIndex,action=kind==2?0:kind>2?kind-1:kind;
                screenHealthText.Visibility=action==0?Visibility.Visible:Visibility.Collapsed;
                for(int i=0;i<learnActions.Length;i++)learnActions[i].Visibility=i==action?Visibility.Visible:Visibility.Collapsed;
            }
            RefreshLearning();screenRegion.SelectionChanged+=(s,e)=>RefreshLearning();calibration.Children.Add(learning);
            var savedButtons=new WrapPanel();savedButtons.Children.Add(Action("Save calibration",()=>{SaveScreenProfile();SetScreenStatus("Calibration saved on this computer");return Task.CompletedTask;}));
            screenUndoCalibration=Action("Undo calibration",UndoScreenCalibration,()=>screenCalibrationUndo.Count>0);screenUndoCalibration.IsEnabled=false;savedButtons.Children.Add(screenUndoCalibration);
            savedButtons.Children.Add(Action("Reset calibration",ResetScreenCalibration));savedButtons.Children.Add(ScreenCalibrationFileMenu());calibration.Children.Add(savedButtons);
            var coordinates=new WrapPanel();var coords=new[]{Input("26.5",70),Input("91.5",70),Input("6.5",70),Input("5.5",70)};var names=new[]{"Left %","Top %","Width %","Height %"};for(int i=0;i<4;i++){Field(coordinates,names[i],coords[i]);Accessible(coords[i],names[i]);}
            coordinates.Children.Add(Action("Apply region",async()=>{await DisarmScreen();double[] n=coords.Select(c=>double.Parse(c.Text,CultureInfo.InvariantCulture)/100).ToArray();SetScreenRegion(new HudRegion { X=n[0],Y=n[1],Width=n[2],Height=n[3] });}));
            var coordinateExpander=new Expander { Header="Region coordinates",Content=coordinates,Margin=new Thickness(0,8,0,0) };screenRefreshCoordinates=()=>{var r=SelectedScreenRegion;double[] a={r.X,r.Y,r.Width,r.Height};for(int i=0;i<4;i++)coords[i].Text=(a[i]*100).ToString("0.###",CultureInfo.InvariantCulture);};coordinateExpander.Expanded+=(s,e)=>screenRefreshCoordinates();screenRegion.SelectionChanged+=(s,e)=>{screenRefreshCoordinates();screenPreview.SelectedKind=screenRegion.SelectedIndex;screenPreview.InvalidateVisual();regionHelp.Text=screenRegion.SelectedIndex switch {
                2=>"Freeze, then select shield digits only. Exclude the shield outline; leave room for 50.",
                1=>"Freeze on a kill confirmation, then select its badge.",
                3=>"Freeze while spectating, then select fixed spectator text. Exclude player names.",
                4=>"Freeze after death, then select KILLED YOU or RESPAWNING IN. Exclude the countdown.",
                _=>"Freeze, then select health digits only. Leave room for 100."
            };};calibration.Children.Add(coordinateExpander);body.Children.Add(StudioPanel(new Expander { Header="Calibration",Content=calibration,IsExpanded=screenProfile.Digits.Count==0 }));
            var feedback=new StackPanel();feedback.Children.Add(Text("Feedback",18));var levels=new WrapPanel();
            AddScreenLevel(levels,"Damage",screenProfile.DamageLevel,v=>screenProfile.DamageLevel=v);AddScreenLevel(levels,"Kill",screenProfile.KillLevel,v=>screenProfile.KillLevel=v);AddScreenLevel(levels,"Death",screenProfile.DeathLevel,v=>screenProfile.DeathLevel=v);feedback.Children.Add(levels);
            screenFeedbackStatus=Text("Saved feedback could not be read. Using defaults.",13);screenFeedbackStatus.Visibility=screenFeedbackLoadFailed?Visibility.Visible:Visibility.Collapsed;feedback.Children.Add(screenFeedbackStatus);
            feedback.Children.Add(Text("Motors · maximum level",14));screenNoMotors=Text("Connect a device to enable feedback. Preview works without one.",13);feedback.Children.Add(screenNoMotors);screenMotorPanel=new StackPanel();feedback.Children.Add(screenMotorPanel);
            screenOutput=Action("Enable output",async()=>{if(screenArmed)await DisarmScreen();else await ArmScreen();});feedback.Children.Add(screenOutput);
            feedback.Children.Add(Text("Follows the selected window even while Alt+Tabbed. Disable output to stop feedback.",13));body.Children.Add(StudioPanel(feedback));
            screenHistory=Text("Events will appear here. Preview never sends device output.",14);body.Children.Add(screenHistory);
            body.Children.Add(Text("Use your own live HUD. Spectating, replays, overlays, HDR and healing/decay can confuse recognition. Screen capture is not Riot approval.",12));
            var tab=new TabItem { Header="Screen sync",Content=new ScrollViewer { Content=body,VerticalScrollBarVisibility=ScrollBarVisibility.Auto,HorizontalScrollBarVisibility=ScrollBarVisibility.Disabled } };tabs.Items.Insert(Math.Min(3,tabs.Items.Count),tab);
            body.Loaded+=(s,e)=>{if(screenWindows.Items.Count==0)RefreshScreenWindows();};
            screenTimer=new DispatcherTimer { Interval=TimeSpan.FromMilliseconds(100) };screenTimer.Tick+=async(s,e)=>{if(screenTickBusy)return;screenTickBusy=true;try{await ScreenTick();}catch{await DisarmScreen();SetScreenStatus("Recognition interrupted · output off");}finally{screenTickBusy=false;}};screenTimer.Start();
        }
        void AddScreenLevel(Panel parent,string label,int initial,Action<int> set)
        {
            var panel=new StackPanel { Width=150,Margin=new Thickness(0,0,16,10) };var value=Text(label+" · "+initial+" / 20",14);panel.Children.Add(value);var slider=new Slider { Minimum=0,Maximum=20,Value=initial,TickFrequency=1,IsSnapToTickEnabled=true };Accessible(slider,label+" event strength");slider.ValueChanged+=async(s,e)=>{var stopped=DisarmScreen();set((int)slider.Value);value.Text=label+" · "+(int)slider.Value+" / 20";SaveScreenFeedback();await stopped;};panel.Children.Add(slider);parent.Children.Add(panel);
        }
        void RefreshScreenWindows()
        {
            var old=(screenWindows.SelectedItem as CaptureWindow)?.Handle??IntPtr.Zero;screenWindows.Items.Clear();foreach(var w in ScreenCapture.Windows(new WindowInteropHelper(window).Handle))screenWindows.Items.Add(w);
            screenWindows.SelectedItem=screenWindows.Items.Cast<CaptureWindow>().FirstOrDefault(w=>w.Handle==old)??screenWindows.Items.Cast<CaptureWindow>().FirstOrDefault(w=>w.Title.IndexOf("VALORANT",StringComparison.OrdinalIgnoreCase)>=0);
        }
        async Task StartScreenPreview()
        {
            var selected=screenWindows.SelectedItem as CaptureWindow;if(selected==null)throw new ArgumentException("Choose a window first.");
            var stopped=StopScreenCapture();int request=screenCaptureRequest;await stopped;
            if(request!=screenCaptureRequest||closing)return;
            ResetScreenDetection();screenFrozen=false;screenPreview.CanSelect=false;screenFreeze.Content="Freeze";screenFailure=null;
            var capture=new ScreenCapture(selected.Handle);screenCapture=capture;
            capture.Frame+=frame=>{if(ReferenceEquals(screenCapture,capture))Interlocked.Exchange(ref screenPending,new ScreenSample { Source=capture,Pixels=frame });};
            capture.Failed+=message=>{if(ReferenceEquals(screenCapture,capture))Interlocked.Exchange(ref screenFailure,message);};
            SetScreenStatus("Preview only · output off");screenLastGood=-1;
        }
        async Task StopScreenCapture()
        {
            screenCaptureRequest++;screenFrozen=false;if(screenFreeze!=null){screenFreeze.Content="Freeze";screenFreeze.IsEnabled=true;}if(screenPreview!=null)screenPreview.CanSelect=false;
            var stopped=DisarmScreen();var capture=screenCapture;screenCapture=null;capture?.Dispose();Interlocked.Exchange(ref screenPending,null);screenFrame=null;screenPreview?.Show(null);screenFilter?.Reset();screenSpectatorFilter?.Reset();screenDeathMarkerFilter?.Reset();
            if(screenReading!=null)screenReading.Text="Health —    Badge —";
            if(screenStatus!=null)SetScreenStatus("Capture off · output off");await stopped;
        }
        async Task ToggleScreenFreeze()
        {
            if(screenCapture==null||screenFrame==null)throw new ArgumentException("Start preview before freezing a frame.");
            var stopped=DisarmScreen();int generation=screenGeneration;await stopped;
            if(generation!=screenGeneration||screenCapture==null||screenFrame==null)return;
            screenFrozen=!screenFrozen;ResetScreenDetection();screenFreeze.Content=screenFrozen?"Resume preview":"Freeze";screenPreview.CanSelect=screenFrozen;
            SetScreenStatus(screenFrozen?"Frozen · drag around the selected HUD element":"Preview only · output off");
        }
        void RequireFrozen(){if(!screenFrozen||screenFrame==null)throw new ArgumentException("Freeze a live preview or open a HUD image before learning.");}
        Task LearnScreenBadge()
        {
            RequireFrozen();var mask=HudVision.Normalize(HudVision.Extract(screenFrame,screenProfile.Badge),32,32);
            int ink=mask?.Count(x=>x>0)??0;
            if(ink<24||ink>800)throw new ArgumentException("Select a visible kill confirmation badge with a little surrounding background.");
            if(screenProfile.BadgeMask?.SequenceEqual(mask)==true||screenProfile.BadgeVariants.Any(m=>m.SequenceEqual(mask))) {
                SetScreenStatus("This badge appearance is already learned");return Task.CompletedTask;
            }
            if(screenProfile.BadgeVariants.Count==7)throw new ArgumentException("Eight badge appearances are learned. Use More > Clear learned badges to start a new set.");
            RememberScreenCalibration();
            if(screenProfile.BadgeMask!=null)screenProfile.BadgeVariants.Add(screenProfile.BadgeMask);
            screenProfile.BadgeMask=mask;
            SetScreenStatus("Badge learned: "+(1+screenProfile.BadgeVariants.Count)+" appearances. Save calibration to keep them");
            RefreshScreenReading();return Task.CompletedTask;
        }
        Task ClearScreenBadges()
        {
            var stopped=DisarmScreen();
            if(screenProfile.BadgeMask==null){SetScreenStatus("No learned badges to clear");return stopped;}
            RememberScreenCalibration();screenProfile.BadgeMask=null;screenProfile.BadgeVariants.Clear();RefreshScreenReading();
            SetScreenStatus("Learned badges cleared. Undo restores them");return stopped;
        }
        void ResetScreenDetection(){screenFilter.Reset();screenSpectatorFilter.Reset();screenDeathMarkerFilter.Reset();}
        Task LearnScreenSpectator()
        {
            RequireFrozen();var mask=HudVision.Normalize(HudVision.Extract(screenFrame,screenProfile.Spectator,false),128,32);
            int ink=mask?.Count(x=>x>0)??0;
            if(ink<48||ink>3000)throw new ArgumentException("Select the visible Switch player marker with a little surrounding background.");
            RememberScreenCalibration();screenProfile.SpectatorMask=mask;RefreshScreenCalibration();
            SetScreenStatus("Spectator marker learned. Save calibration to keep it");return Task.CompletedTask;
        }
        Task ClearScreenSpectator()
        {
            var stopped=DisarmScreen();
            if(screenProfile.SpectatorMask!=null){RememberScreenCalibration();screenProfile.SpectatorMask=null;RefreshScreenCalibration();}
            SetScreenStatus("Spectator marker cleared");return stopped;
        }
        Task LearnScreenDeathMarker()
        {
            RequireFrozen();var mask=HudVision.Normalize(HudVision.ExtractDeathText(screenFrame,screenProfile.DeathMarker),128,32);
            int ink=mask?.Count(x=>x>0)??0;
            if(ink<48||ink>3000)throw new ArgumentException("Select the fixed death text: KILLED YOU or RESPAWNING IN. Exclude the countdown.");
            RememberScreenCalibration();screenProfile.DeathMarkerMask=mask;screenProfile.DeathMarkerFrameWidth=screenFrame.Width;screenProfile.DeathMarkerFrameHeight=screenFrame.Height;RefreshScreenCalibration();
            SetScreenStatus("Death text learned. Save calibration to keep it");return Task.CompletedTask;
        }
        Task ClearScreenDeathMarker()
        {
            var stopped=DisarmScreen();
            if(screenProfile.DeathMarkerMask!=null){RememberScreenCalibration();screenProfile.DeathMarkerMask=null;screenProfile.DeathMarkerFrameWidth=0;screenProfile.DeathMarkerFrameHeight=0;RefreshScreenCalibration();}
            SetScreenStatus("Death text cleared");return stopped;
        }
        HudRegion SelectedScreenRegion=>screenRegion.SelectedIndex==0?screenProfile.Health:screenRegion.SelectedIndex==2?screenProfile.Shield:screenRegion.SelectedIndex==3?screenProfile.Spectator:screenRegion.SelectedIndex==4?screenProfile.DeathMarker:screenProfile.Badge;
        Task LearnScreenDigits()
        {
            RequireFrozen();if(screenRegion.SelectedIndex!=0&&screenRegion.SelectedIndex!=2)throw new ArgumentException("Choose Health or Shield before learning digits.");
            var learned=HudVision.Learn(screenFrame,SelectedScreenRegion,screenHealthText.Text.Trim(),screenRegion.SelectedIndex==2?50:100);
            RememberScreenCalibration();screenProfile.Digits.AddRange(learned);while(screenProfile.Digits.Count>80)screenProfile.Digits.RemoveAt(0);
            SetScreenStatus("Digits learned · Save calibration to keep them");RefreshScreenReading();return Task.CompletedTask;
        }
        Task SetScreenShieldEnabled(bool enabled)
        {
            var stopped=DisarmScreen();
            if(screenProfile.ReadShield!=enabled){RememberScreenCalibration();screenProfile.ReadShield=enabled;RefreshScreenCalibration();SetScreenStatus("Shield reading "+(enabled?"enabled":"disabled")+" · Save calibration to keep it");}
            return stopped;
        }
        void RememberScreenCalibration()
        {
            // Independent copies: later learning and edits must not mutate the undo state.
            screenCalibrationUndo.Add(json.Deserialize<ScreenProfile>(json.Serialize(screenProfile)));
            if(screenCalibrationUndo.Count>20)screenCalibrationUndo.RemoveAt(0);
            screenUndoCalibration.IsEnabled=true;
        }
        void RefreshScreenCalibration()
        {
            screenCalibrationRefreshing=true;try{screenReadShield.IsChecked=screenProfile.ReadShield;if(screenReadKillFeed!=null)screenReadKillFeed.IsChecked=screenProfile.ReadKillFeed;}finally{screenCalibrationRefreshing=false;}
            ResetScreenDetection();screenRefreshCoordinates?.Invoke();screenPreview.InvalidateVisual();RefreshScreenReading();
            screenUndoCalibration.IsEnabled=screenCalibrationUndo.Count>0;
        }
        void SetScreenRegion(HudRegion region)
        {
            if(region==null||!region.Valid)throw new ArgumentException("Keep the region inside the image (0–100%).");
            RememberScreenCalibration();
            if(screenRegion.SelectedIndex==0)screenProfile.Health=region;
            else if(screenRegion.SelectedIndex==2)screenProfile.Shield=region;
            else if(screenRegion.SelectedIndex==3){screenProfile.Spectator=region;screenProfile.SpectatorMask=null;}
            else if(screenRegion.SelectedIndex==4){screenProfile.DeathMarker=region;screenProfile.DeathMarkerMask=null;screenProfile.DeathMarkerFrameWidth=0;screenProfile.DeathMarkerFrameHeight=0;}
            else {screenProfile.Badge=region;screenProfile.BadgeMask=null;screenProfile.BadgeVariants.Clear();}
            RefreshScreenCalibration();
            SetScreenStatus("Region changed · Save calibration to keep it");
        }
        Task ResetScreenCalibration()
        {
            var stopped=DisarmScreen();RememberScreenCalibration();
            screenProfile.Health=new ScreenProfile().Health;screenProfile.Badge=new ScreenProfile().Badge;screenProfile.Shield=new ScreenProfile().Shield;screenProfile.ReadShield=false;screenProfile.ReadKillFeed=false;
            screenProfile.BadgeMask=null;screenProfile.BadgeVariants.Clear();screenProfile.Digits.Clear();screenProfile.Spectator=new ScreenProfile().Spectator;screenProfile.SpectatorMask=null;screenProfile.DeathMarker=new ScreenProfile().DeathMarker;screenProfile.DeathMarkerMask=null;screenProfile.DeathMarkerFrameWidth=0;screenProfile.DeathMarkerFrameHeight=0;
            SaveScreenProfile();RefreshScreenCalibration();SetScreenStatus("Calibration reset · Undo restores the previous setup");
            return stopped;
        }
        Task UndoScreenCalibration()
        {
            var stopped=DisarmScreen();
            if(screenCalibrationUndo.Count==0)return stopped;
            var previous=screenCalibrationUndo[screenCalibrationUndo.Count-1];screenCalibrationUndo.RemoveAt(screenCalibrationUndo.Count-1);
            // Feedback levels are independent settings, not part of calibration undo.
            screenProfile.Health=previous.Health;screenProfile.Badge=previous.Badge;
            screenProfile.Shield=previous.Shield;screenProfile.ReadShield=previous.ReadShield;screenProfile.ReadKillFeed=previous.ReadKillFeed;
            screenProfile.Spectator=previous.Spectator;screenProfile.SpectatorMask=previous.SpectatorMask;screenProfile.DeathMarker=previous.DeathMarker;screenProfile.DeathMarkerMask=previous.DeathMarkerMask;screenProfile.DeathMarkerFrameWidth=previous.DeathMarkerFrameWidth;screenProfile.DeathMarkerFrameHeight=previous.DeathMarkerFrameHeight;
            screenProfile.BadgeMask=previous.BadgeMask;screenProfile.BadgeVariants=previous.BadgeVariants;screenProfile.Digits=previous.Digits;
            RefreshScreenCalibration();SetScreenStatus("Calibration restored · Save calibration to keep it");
            return stopped;
        }
        async Task SetScreenKillFeed(bool enabled)
        {
            var stopped=DisarmScreen();
            if(screenProfile.ReadKillFeed!=enabled){RememberScreenCalibration();screenProfile.ReadKillFeed=enabled;RefreshScreenCalibration();SetScreenStatus("Kill source: "+(enabled?"feed - gold killer outlines":"learned badges")+" - Save calibration to keep it");}
            await stopped;
        }
        void SaveScreenProfile(){if(!screenProfile.Validate())throw new ArgumentException("Invalid calibration.");Directory.CreateDirectory(Path.GetDirectoryName(ScreenProfilePath));var temp=ScreenProfilePath+".tmp";File.WriteAllText(temp,json.Serialize(screenProfile));File.Move(temp,ScreenProfilePath,true);}
        HudReading RefreshScreenReading()
        {
            if(screenFrame==null)return null;var read=HudVision.Read(screenFrame,screenProfile,screenGlyphs);
            screenReading.Text="Health "+(read.Health?.ToString()??"—")+" · "+Math.Round(read.Confidence*100)+"% match"+(screenProfile.ReadShield?"    Shield "+(read.Shield?.ToString()??"—")+" · "+Math.Round(read.ShieldConfidence*100)+"% match":"")+(screenProfile.ReadKillFeed?"    Kill feed "+read.KillFeed.Length+" personal rows":"    Badge "+(screenProfile.BadgeMask==null?"not calibrated":Math.Round(read.BadgeScore*100)+"% match · "+(1+screenProfile.BadgeVariants.Count)+" learned"))+(screenProfile.SpectatorMask==null?"":"    Spectator "+Math.Round(read.SpectatorScore*100)+"% match")+(screenProfile.DeathMarkerMask==null?"":"    Death text "+(screenProfile.DeathMarkerFrameWidth>0&&(screenProfile.DeathMarkerFrameWidth!=screenFrame.Width||screenProfile.DeathMarkerFrameHeight!=screenFrame.Height)?"relearn for "+screenFrame.Width+" × "+screenFrame.Height:Math.Round(read.DeathMarkerScore*100)+"% match"));return read;
        }
        static bool ScreenChannelAvailable(Device device,int channel)=>device.Connected&&device.Player!=null&&device.Motors.ContainsKey(channel)&&device.Channels.Contains(channel);
        void RefreshScreenRoutes()
        {
            foreach(var d in devices.Values.Where(d=>d.Connected&&d.Player!=null))foreach(int channel in d.Motors.Keys) {
                string key=MixerKey(d,channel);if(screenRoutes.ContainsKey(key))continue;
                var saved=screenFeedback.Motors.TryGetValue(ScreenFeedbackKey(d,channel),out var preferences)?preferences:new ScreenMotorFeedback();
                var route=new ScreenRoute { Device=d,Channel=channel,Include=new CheckBox { Content=d.Name+" · "+d.ChannelLabel(channel),IsChecked=!screenArmed&&saved.Enabled,MinWidth=150,Margin=new Thickness(0,0,16,0) },Ceiling=new Slider { Minimum=0,Maximum=20,Value=saved.Maximum,Width=130,TickFrequency=1,IsSnapToTickEnabled=true } };
                Accessible(route.Include,d.Name+" screen sync "+d.ChannelLabel(channel));Accessible(route.Ceiling,d.Name+" screen sync "+d.ChannelLabel(channel)+" maximum level");
                var group=new StackPanel { Margin=new Thickness(0,8,0,12) };var row=new WrapPanel();row.Children.Add(route.Include);row.Children.Add(route.Ceiling);var value=Text(saved.Maximum+" / 20",13);value.Margin=new Thickness(12,0,0,0);row.Children.Add(value);group.Children.Add(row);
                var events=new WrapPanel { Margin=new Thickness(0,6,0,0) };
                foreach(string kind in new[]{"Damage","Kill","Death"}) {
                    var choice=new CheckBox { Content=kind,IsChecked=(saved.Events&ScreenMotorFeedback.EventMask(kind))!=0,Margin=new Thickness(0,0,20,6),MinHeight=28,VerticalContentAlignment=VerticalAlignment.Center };
                    Accessible(choice,d.Name+" screen sync "+d.ChannelLabel(channel)+" "+kind.ToLowerInvariant()+" feedback");route.Events.Add(kind,choice);events.Children.Add(choice);
                    choice.Checked+=async(s,e)=>{var stopped=DisarmScreen();SaveScreenRoute(route);await stopped;};choice.Unchecked+=async(s,e)=>{var stopped=DisarmScreen();SaveScreenRoute(route);await stopped;};
                }
                group.Children.Add(events);screenMotorPanel.Children.Add(group);screenRoutes[key]=route;
                route.Include.Checked+=async(s,e)=>{var stopped=DisarmScreen();SaveScreenRoute(route);await stopped;};route.Include.Unchecked+=async(s,e)=>{var stopped=DisarmScreen();SaveScreenRoute(route);await stopped;};route.Ceiling.ValueChanged+=async(s,e)=>{value.Text=(int)route.Ceiling.Value+" / 20";var stopped=DisarmScreen();SaveScreenRoute(route);await stopped;};
            }
            foreach(var route in screenRoutes.Values)route.Include.IsEnabled=ScreenChannelAvailable(route.Device,route.Channel);
            screenNoMotors.Visibility=screenRoutes.Values.Any(r=>ScreenChannelAvailable(r.Device,r.Channel))?Visibility.Collapsed:Visibility.Visible;
        }
        Task ArmScreen()=>ArmScreenCore(null);
        async Task ArmScreenCore(Func<bool> allowed)
        {
            if(allowed!=null&&!allowed())return;
            if(screenCapture==null||screenFrozen||screenFrame==null||ScreenCapture.Now-screenFrame.Time>.75)throw new ArgumentException("Start a live preview first.");
            var read=RefreshScreenReading();if(read?.Health==null||read.Health<=0)throw new ArgumentException("Calibrate health until the preview reads it correctly before enabling output.");
            if(read.SpectatorScore>=.85)throw new ArgumentException("Spectator marker visible. Return to your own HUD before enabling output.");
            var selected=screenRoutes.Values.Where(r=>r.Include.IsChecked==true&&ScreenChannelAvailable(r.Device,r.Channel)&&r.Ceiling.Value>0&&ScreenRouteEvents(r)!=0)
                .Select(r=>new ScreenTarget {Device=r.Device,Channel=r.Channel,Generation=r.Device.Generation,Player=r.Device.Player,Ceiling=(int)r.Ceiling.Value,Events=ScreenRouteEvents(r)}).ToArray();
            if(selected.Length==0)throw new ArgumentException("Select a connected motor with an enabled event and a maximum above zero.");
            bool TargetsCurrent()=>selected.All(t=>ScreenChannelAvailable(t.Device,t.Channel)&&t.Device.Generation==t.Generation&&ReferenceEquals(t.Device.Player,t.Player));
            await ManualGuarded(async()=> {
                if(allowed!=null&&!allowed())return;
                if(!TargetsCurrent()){SetScreenStatus("Device connection changed · enable output again");return;}
                int operation=operatorGeneration,generation=screenGeneration;var capture=screenCapture;
                if(!await StopPlayers())throw new InvalidOperationException("Existing output could not be stopped.");
                if(closing||screenCapture==null||screenFrozen||capture!=screenCapture||operation!=operatorGeneration||generation!=screenGeneration||screenFrame==null||ScreenCapture.Now-screenFrame.Time>.75||allowed!=null&&!allowed())return;
                if(!TargetsCurrent()){SetScreenStatus("Device connection changed · enable output again");return;}
                // The HUD can change while another source or device is stopping.
                // Do not arm using the positive reading taken before those waits.
                var current=RefreshScreenReading();
                if(current?.Health==null||current.Health<=0){SetScreenStatus("HUD changed · verify health before enabling output");return;}
                if(current.SpectatorScore>=.85){SetScreenStatus("Spectator marker visible · output off");return;}
                screenOwned.Clear();screenOwned.AddRange(selected);
                screenOperation=operatorGeneration;screenGeneration++;screenArmed=true;screenDeathUntil=0;screenWaiting=false;screenResumeFrames=0;screenLastGood=ScreenCapture.Now;ResetScreenDetection();screenOutput.Content="Disable output";SetScreenStatus("Following HUD · output enabled (including background)");
            },()=>screenGeneration);
        }
        async Task DisarmScreen()
        {
            bool wasArmed=screenArmed;
            screenArmed=false;screenWaiting=false;screenResumeFrames=0;screenGeneration++;
            if(screenOutput!=null)screenOutput.Content="Enable output";
            // Capture exact playback instances before awaiting; never stop a replacement device.
            var targets=screenOwned?.ToArray()??Array.Empty<ScreenTarget>();screenOwned?.Clear();
            if(screenStatus!=null&&(wasArmed||targets.Length>0))screenStatus.Text="Stopping output…";
            await StopScreenTargets(targets);
        }
        async Task StopScreenTargets(ScreenTarget[] targets)
        {
            await Task.WhenAll(targets.Select(async t=>{
                var attempt=new ScreenStopAttempt { Target=t,Pending=true };screenStopRetries.Add(attempt);
                try{var task=t.Player.StopTracked(t.Channel,out var revision);attempt.Revision=revision;await task;}
                catch{if(screenStatus!=null&&!screenArmed&&attempt.Current)screenStatus.Text="Output stop unconfirmed · check device connection";}
                finally{attempt.Pending=false;if(!attempt.Current)screenStopRetries.Remove(attempt);}
            }));
            RefreshScreenStopStatus();
        }
        void RefreshScreenStopStatus()
        {
            if(screenArmed&&!screenWaiting||screenStatus==null||screenStopRetries.Any(a=>a.Pending))return;
            if(screenStopRetries.Any(a=>a.Current)){screenStatus.Text="Output stop unconfirmed · retrying";return;}
            // A newer calibration or capture message must survive a late stop.
            if(screenStatus.Text=="Stopping output…"||screenStatus.Text.StartsWith("Output stop unconfirmed",StringComparison.Ordinal))
                screenStatus.Text=screenWaiting?"Waiting for live HUD · output remains enabled":screenCapture==null?"Capture off · output off":screenFrozen?"Frozen · output off":"Preview only · output off";
        }
        void SetScreenStatus(string message)
        {
            if(screenStatus==null)return;
            screenStatus.Text=message;
            // A capture or calibration message must not conceal an outstanding stop.
            if((!screenArmed||screenWaiting)&&screenStopRetries.Any(a=>a.Pending))screenStatus.Text="Stopping output…";
            else RefreshScreenStopStatus();
        }
        async Task RetryScreenStops(double now)
        {
            if(screenStopRetries.Count==0)return;
            if(now>=screenNextStopRetry) {
                screenNextStopRetry=now+.5;
                foreach(var attempt in screenStopRetries.ToArray()) {
                    if(attempt.Pending)continue;
                    if(!attempt.Current){screenStopRetries.Remove(attempt);continue;}
                    attempt.Pending=true;
                    try{var task=attempt.Target.Player.RetryStop(attempt.Target.Channel,attempt.Revision,out var claim);attempt.Revision=claim;await task;}catch{}
                    finally{attempt.Pending=false;if(!attempt.Current)screenStopRetries.Remove(attempt);}
                }
            }
            RefreshScreenStopStatus();
        }
        async Task ScreenTick()
        {
            RefreshScreenRoutes();double now=ScreenCapture.Now;
            await RetryScreenStops(now);
            var failure=Interlocked.Exchange(ref screenFailure,null);if(failure!=null){var stop=StopScreenCapture();int request=screenCaptureRequest;await stop;if(request==screenCaptureRequest)SetScreenStatus(failure);return;}
            int captureRequest=screenCaptureRequest;
            async Task<bool> StopForTick(string message)
            {
                var stop=DisarmScreen();int generation=screenGeneration;await stop;
                // A stop can wait for a device while the user replaces or stops capture.
                // The old tick must not restore its frame or overwrite the newer state.
                if(captureRequest!=screenCaptureRequest||generation!=screenGeneration||closing)return false;
                SetScreenStatus(message);return true;
            }
            async Task<bool> WaitForTick(string message)
            {
                if(!screenArmed)return true;
                if(!screenWaiting) {
                    screenWaiting=true;screenResumeFrames=0;screenGeneration++;ResetScreenDetection();
                    int generation=screenGeneration;
                    await StopScreenTargets(screenOwned.ToArray());
                    if(captureRequest!=screenCaptureRequest||generation!=screenGeneration||closing)return false;
                }
                SetScreenStatus(message);return true;
            }
            var sample=Interlocked.Exchange(ref screenPending,null);var frame=sample!=null&&ReferenceEquals(sample.Source,screenCapture)?sample.Pixels:null;
            if(screenArmed) {
                // A successful cleanup stop must not erase a failed cue. The flag
                // belongs to this arm's target, so old jobs cannot disarm a new owner.
                if(screenOwned.Any(t=>Volatile.Read(ref t.CommandFailed)!=0)) {if(!await StopForTick("Device command failed · output off"))return;}
                if(screenDeathUntil>0&&now>=screenDeathUntil){screenDeathUntil=0;if(!await WaitForTick("Waiting for respawn · output remains enabled"))return;}
                if(screenOperation!=operatorGeneration||screenCapture==null||screenOwned.Any(t=>!ScreenChannelAvailable(t.Device,t.Channel)||t.Device.Generation!=t.Generation||t.Device.Player!=t.Player||!screenWaiting&&!t.Player.Active(t.Channel)&&t.Player.Level(t.Channel)<0)) {if(!await StopForTick("Source or device changed · output off"))return;}
                if(screenArmed&&(screenFrozen||frame==null||now-frame.Time>.5)&&(screenFrame==null||now-screenFrame.Time>.75||screenLastGood>=0&&now-screenLastGood>1)) {if(!await WaitForTick("Waiting for live HUD · output remains enabled"))return;}
            }
            if(screenFrozen||frame==null)return;
            if(now-frame.Time>.5){ResetScreenDetection();return;}
            if(screenFrame!=null&&(screenFrame.Width!=frame.Width||screenFrame.Height!=frame.Height)){if(!await WaitForTick("Window resized · waiting for live HUD"))return;ResetScreenDetection();}
            screenFrame=frame;if(screenPreview.IsVisible)screenPreview.Show(frame);var read=RefreshScreenReading();if(read?.Health.HasValue==true)screenLastGood=now;
            // Evaluate a fresh pending sample before expiring the previous HUD reading.
            // Unknown health still expires on schedule; a newly readable sample can recover.
            if(screenArmed&&screenLastGood>=0&&now-screenLastGood>1)
                {if(!await WaitForTick("Waiting for live HUD · output remains enabled"))return;}
            bool spectator=screenSpectatorFilter.Push(read?.SpectatorScore??0,frame.Time);
            if(spectator) {
                screenFilter.Reset();screenDeathMarkerFilter.Reset();
                if(!await WaitForTick("Spectating detected · waiting for your HUD"))return;
                return;
            }
            // Do not queue a new cue while a spectator marker is being confirmed.
            if(read?.SpectatorScore>=.85){screenResumeFrames=0;screenFilter.Reset();screenDeathMarkerFilter.Reset();return;}
            if(screenWaiting) {
                bool ready=read?.Health>0&&read.Confidence>=.82&&read.SpectatorScore<.85&&read.DeathMarkerScore<.85;
                screenResumeFrames=ready?screenResumeFrames+1:0;
                if(screenResumeFrames<3||screenStopRetries.Any(a=>a.Pending||a.Current))return;
                screenWaiting=false;screenResumeFrames=0;screenLastGood=now;ResetScreenDetection();
            }
            var events=screenFilter.Push(read,frame.Time).ToList();
            if(screenDeathMarkerFilter.Push(read,read?.DeathMarkerScore??0,frame.Time)&&!events.Any(e=>e.Kind=="Death"))
                {screenFilter.Reset();events.Add(new HudEvent {Kind="Death",Health=0});}
            foreach(var ev in HudEvent.CombineKills(events).OrderByDescending(e=>ScreenMotorFeedback.EventPriority(e.Kind))) {
                string description=ev.Kind=="Damage"?"Damage · −"+(ev.Amount-ev.ShieldAmount)+" HP"+(ev.ShieldAmount>0?" · −"+ev.ShieldAmount+" shield":""):ev.Kind=="Kill"&&ev.Count>1?"Kill × "+ev.Count:ev.Kind;
                string outcome="preview";
                if(screenArmed&&!screenWaiting)outcome=screenDeathUntil>0?"output ending":screenCueBusy?"busy":await SendScreenCue(ev);
                screenEvents.Enqueue(DateTime.Now.ToString("HH:mm:ss")+"  "+description+" · "+outcome);while(screenEvents.Count>8)screenEvents.Dequeue();screenHistory.Text=string.Join(Environment.NewLine,screenEvents.Reverse());
                if(ev.Kind=="Death"&&screenArmed)screenDeathUntil=now+.4;
            }
            if(screenArmed&&!screenWaiting)SetScreenStatus("Following HUD · output enabled");
        }
        async Task<string> SendScreenCue(HudEvent ev)
        {
            int generation=screenGeneration;screenCueBusy=true;
            try {
                int high=ev.Kind=="Kill"?screenProfile.KillLevel:ev.Kind=="Death"?screenProfile.DeathLevel:Math.Max(1,(int)Math.Round(screenProfile.DamageLevel*(.35+.65*(100-ev.Health)/100.0)));
                if(ev.Kind=="Damage"&&screenProfile.DamageLevel==0)high=0;
                double now=ScreenCapture.Now;int priority=ScreenMotorFeedback.EventPriority(ev.Kind);
                var clock=Stopwatch.StartNew();var starts=new List<Task>();int cooling=0;bool sourceChanged=false;
                foreach(var t in screenOwned.ToArray()) {
                    if((t.Events&ScreenMotorFeedback.EventMask(ev.Kind))==0)continue;
                    bool Current()=>screenArmed&&generation==screenGeneration&&operatorGeneration==screenOperation&&screenCapture!=null&&!screenWaiting&&ScreenChannelAvailable(t.Device,t.Channel)&&t.Device.Generation==t.Generation&&ReferenceEquals(t.Device.Player,t.Player)&&screenFrame!=null&&ScreenCapture.Now-screenFrame.Time<.75;
                    if(!Current()){sourceChanged=true;break;}
                    int level=Math.Min(t.Ceiling,high);if(level==0)continue;
                    // Cooldowns belong to motors: one event must not silence a different route.
                    // On a shared motor, a higher-priority event can replace the current cue.
                    // The detector already deduplicates kills. A distinct new kill can
                    // retrigger its reward, but cannot interrupt a higher-priority death.
                    if(now-t.LastCue<.45&&(priority<t.Priority||priority==t.Priority&&ev.Kind!="Kill")){cooling++;continue;}
                    // Short hit pulses must begin above zero. A zero-valued ramp
                    // sample plus a Bluetooth ACK can consume their whole deadline.
                    var pattern=ev.Kind=="Kill"?ScreenKillPattern(level,ev.Count):new PatternSpec { mode="Steady",high=level,durationMs=ev.Kind=="Death"?350:220,transitionMs=0 };
                    t.LastCue=now;t.Priority=priority;
                    pattern.stopAtMs=pattern.durationMs;starts.Add(t.Player.Start(t.Channel,pattern,20,clock,Current,()=>Interlocked.Exchange(ref t.CommandFailed,1)));
                }
                await Task.WhenAll(starts);
                if(!screenArmed||generation!=screenGeneration||operatorGeneration!=screenOperation)return "cancelled";
                if(sourceChanged)return "source changed";
                return starts.Count>0?"queued · "+starts.Count+(starts.Count==1?" motor":" motors"):cooling>0?"cooldown":"muted";
            }catch{await DisarmScreen();SetScreenStatus("Device command failed · output off");return "command failed";}finally{screenCueBusy=false;}
        }
        static PatternSpec ScreenKillPattern(int level,int count)
        {
            count=Math.Clamp(count,1,6);
            var frames=new List<Frame> {new Frame {ms=0,level=Math.Max(1,level*3/4)},new Frame {ms=140,level=0}};
            for(int i=0;i<count;i++){frames.Add(new Frame {ms=250+i*300,level=level});frames.Add(new Frame {ms=450+i*300,level=0});}
            int duration=450+(count-1)*300;
            return new PatternSpec {mode="Custom",durationMs=duration,periodMs=duration,repeat=false,stopAtMs=duration,frames=frames.ToArray()};
        }
    }
}

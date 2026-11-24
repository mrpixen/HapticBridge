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
        sealed class ScreenTarget { public Device Device;public int Channel,Generation,Ceiling,Events;public Playback Player; }
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
        CheckBox screenReadShield;
        bool screenCalibrationRefreshing;
        DispatcherTimer screenTimer;
        bool screenFrozen,screenArmed,screenTickBusy,screenCueBusy,screenForegroundSeen;
        int screenGeneration,screenOperation,screenCaptureRequest;
        double screenArmedAt,screenLastGood=-1,screenLastCue=-10,screenDeathUntil;
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
            screenPreview=new HudPreview { Profile=screenProfile,Margin=new Thickness(0,12,0,4) };Accessible(screenPreview,"HUD preview. Freeze, choose Health, Shield or Kill badge, and drag a region. Numeric coordinates are also available below.");body.Children.Add(screenPreview);
            screenReading=Text("Health —    Badge —",14);body.Children.Add(screenReading);
            var calibration=new StackPanel();
            var selection=new WrapPanel();screenRegion=new ComboBox { Width=140,Margin=new Thickness(0,0,10,8) };screenRegion.Items.Add("Health");screenRegion.Items.Add("Kill badge");screenRegion.Items.Add("Shield");screenRegion.SelectedIndex=0;Accessible(screenRegion,"HUD element to select");selection.Children.Add(screenRegion);selection.Children.Add(Text("Freeze, then drag around the digits or badge.",13));calibration.Children.Add(selection);
            screenReadShield=new CheckBox { Content="Read shield damage",IsChecked=screenProfile.ReadShield,Margin=new Thickness(0,0,0,10) };Accessible(screenReadShield,"Read shield damage from the calibrated shield number");calibration.Children.Add(screenReadShield);
            screenReadShield.Checked+=async(s,e)=>{if(!screenCalibrationRefreshing)await SetScreenShieldEnabled(true);};screenReadShield.Unchecked+=async(s,e)=>{if(!screenCalibrationRefreshing)await SetScreenShieldEnabled(false);};
            screenPreview.Selected+=SetScreenRegion;
            var learning=new WrapPanel();screenHealthText=Input("100",75);Accessible(screenHealthText,"Selected health or shield number visible in frozen preview");learning.Children.Add(screenHealthText);
            learning.Children.Add(Action("Learn digits",LearnScreenDigits));
            learning.Children.Add(Action("Learn kill badge",()=>{RequireFrozen();var mask=HudVision.Normalize(HudVision.Extract(screenFrame,screenProfile.Badge),32,32);int ink=mask?.Count(x=>x>0)??0;if(ink<24||ink>800)throw new ArgumentException("Select a visible kill confirmation badge with a little surrounding background.");RememberScreenCalibration();screenProfile.BadgeMask=mask;screenStatus.Text="Badge learned · save calibration to keep it";RefreshScreenReading();return Task.CompletedTask;}));calibration.Children.Add(learning);
            var savedButtons=new WrapPanel();savedButtons.Children.Add(Action("Save calibration",()=>{SaveScreenProfile();screenStatus.Text="Calibration saved on this computer";return Task.CompletedTask;}));
            screenUndoCalibration=Action("Undo calibration",UndoScreenCalibration,()=>screenCalibrationUndo.Count>0);screenUndoCalibration.IsEnabled=false;savedButtons.Children.Add(screenUndoCalibration);
            savedButtons.Children.Add(Action("Reset calibration",ResetScreenCalibration));calibration.Children.Add(savedButtons);
            var coordinates=new WrapPanel();var coords=new[]{Input("26.5",70),Input("91.5",70),Input("6.5",70),Input("5.5",70)};var names=new[]{"Left %","Top %","Width %","Height %"};for(int i=0;i<4;i++){Field(coordinates,names[i],coords[i]);Accessible(coords[i],names[i]);}
            coordinates.Children.Add(Action("Apply region",async()=>{await DisarmScreen();double[] n=coords.Select(c=>double.Parse(c.Text,CultureInfo.InvariantCulture)/100).ToArray();SetScreenRegion(new HudRegion { X=n[0],Y=n[1],Width=n[2],Height=n[3] });}));
            var coordinateExpander=new Expander { Header="Region coordinates",Content=coordinates,Margin=new Thickness(0,8,0,0) };screenRefreshCoordinates=()=>{var r=SelectedScreenRegion;double[] a={r.X,r.Y,r.Width,r.Height};for(int i=0;i<4;i++)coords[i].Text=(a[i]*100).ToString("0.###",CultureInfo.InvariantCulture);};coordinateExpander.Expanded+=(s,e)=>screenRefreshCoordinates();screenRegion.SelectionChanged+=(s,e)=>screenRefreshCoordinates();calibration.Children.Add(coordinateExpander);body.Children.Add(StudioPanel(new Expander { Header="Calibration",Content=calibration,IsExpanded=screenProfile.Digits.Count==0 }));
            var feedback=new StackPanel();feedback.Children.Add(Text("Feedback",18));var levels=new WrapPanel();
            AddScreenLevel(levels,"Damage",screenProfile.DamageLevel,v=>screenProfile.DamageLevel=v);AddScreenLevel(levels,"Kill",screenProfile.KillLevel,v=>screenProfile.KillLevel=v);AddScreenLevel(levels,"Death",screenProfile.DeathLevel,v=>screenProfile.DeathLevel=v);feedback.Children.Add(levels);
            screenFeedbackStatus=Text("Saved feedback could not be read. Using defaults.",13);screenFeedbackStatus.Visibility=screenFeedbackLoadFailed?Visibility.Visible:Visibility.Collapsed;feedback.Children.Add(screenFeedbackStatus);
            feedback.Children.Add(Text("Motors · maximum level",14));screenNoMotors=Text("Connect a device to enable feedback. Preview works without one.",13);feedback.Children.Add(screenNoMotors);screenMotorPanel=new StackPanel();feedback.Children.Add(screenMotorPanel);
            screenOutput=Action("Enable output",async()=>{if(screenArmed)await DisarmScreen();else await ArmScreen();});feedback.Children.Add(screenOutput);
            feedback.Children.Add(Text("Return to the selected window within 8 seconds. Leaving it turns output off.",13));body.Children.Add(StudioPanel(feedback));
            screenHistory=Text("Events will appear here. Preview never sends vibrations.",14);body.Children.Add(screenHistory);
            body.Children.Add(Text("Use your own live HUD. Spectating, replays, overlays, HDR and healing/decay can confuse recognition. Screen capture is not Riot approval.",12));
            var tab=new TabItem { Header="Screen sync",Content=new ScrollViewer { Content=body,VerticalScrollBarVisibility=ScrollBarVisibility.Auto,HorizontalScrollBarVisibility=ScrollBarVisibility.Disabled } };tabs.Items.Insert(Math.Min(3,tabs.Items.Count),tab);
            body.Loaded+=(s,e)=>{if(screenWindows.Items.Count==0)RefreshScreenWindows();};
            screenTimer=new DispatcherTimer { Interval=TimeSpan.FromMilliseconds(100) };screenTimer.Tick+=async(s,e)=>{if(screenTickBusy)return;screenTickBusy=true;try{await ScreenTick();}catch{await DisarmScreen();screenStatus.Text="Recognition interrupted · output off";}finally{screenTickBusy=false;}};screenTimer.Start();
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
            screenFilter.Reset();screenFrozen=false;screenPreview.CanSelect=false;screenFreeze.Content="Freeze";screenFailure=null;
            var capture=new ScreenCapture(selected.Handle);screenCapture=capture;
            capture.Frame+=frame=>{if(ReferenceEquals(screenCapture,capture))Interlocked.Exchange(ref screenPending,new ScreenSample { Source=capture,Pixels=frame });};
            capture.Failed+=message=>{if(ReferenceEquals(screenCapture,capture))Interlocked.Exchange(ref screenFailure,message);};
            screenStatus.Text="Preview only · output off";screenLastGood=-1;
        }
        async Task StopScreenCapture()
        {
            screenCaptureRequest++;screenFrozen=false;if(screenFreeze!=null)screenFreeze.Content="Freeze";if(screenPreview!=null)screenPreview.CanSelect=false;
            var stopped=DisarmScreen();var capture=screenCapture;screenCapture=null;capture?.Dispose();Interlocked.Exchange(ref screenPending,null);screenFrame=null;screenPreview?.Show(null);screenFilter?.Reset();
            if(screenReading!=null)screenReading.Text="Health —    Badge —";
            if(screenStatus!=null)screenStatus.Text="Capture off · output off";await stopped;
        }
        async Task ToggleScreenFreeze()
        {
            if(screenCapture==null||screenFrame==null)throw new ArgumentException("Start preview before freezing a frame.");
            var stopped=DisarmScreen();int generation=screenGeneration;await stopped;
            if(generation!=screenGeneration||screenCapture==null||screenFrame==null)return;
            screenFrozen=!screenFrozen;screenFilter.Reset();screenFreeze.Content=screenFrozen?"Resume preview":"Freeze";screenPreview.CanSelect=screenFrozen;
            screenStatus.Text=screenFrozen?"Frozen · drag around the selected HUD element":"Preview only · output off";
        }
        void RequireFrozen(){if(!screenFrozen||screenFrame==null)throw new ArgumentException("Start preview, then Freeze on the HUD you want to learn.");}
        HudRegion SelectedScreenRegion=>screenRegion.SelectedIndex==0?screenProfile.Health:screenRegion.SelectedIndex==2?screenProfile.Shield:screenProfile.Badge;
        Task LearnScreenDigits()
        {
            RequireFrozen();if(screenRegion.SelectedIndex==1)throw new ArgumentException("Choose Health or Shield before learning digits.");
            var learned=HudVision.Learn(screenFrame,SelectedScreenRegion,screenHealthText.Text.Trim(),screenRegion.SelectedIndex==2?50:100);
            RememberScreenCalibration();screenProfile.Digits.AddRange(learned);while(screenProfile.Digits.Count>80)screenProfile.Digits.RemoveAt(0);
            screenStatus.Text="Digits learned · Save calibration to keep them";RefreshScreenReading();return Task.CompletedTask;
        }
        Task SetScreenShieldEnabled(bool enabled)
        {
            var stopped=DisarmScreen();
            if(screenProfile.ReadShield!=enabled){RememberScreenCalibration();screenProfile.ReadShield=enabled;RefreshScreenCalibration();screenStatus.Text="Shield reading "+(enabled?"enabled":"disabled")+" · Save calibration to keep it";}
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
            screenCalibrationRefreshing=true;try{screenReadShield.IsChecked=screenProfile.ReadShield;}finally{screenCalibrationRefreshing=false;}
            screenFilter.Reset();screenRefreshCoordinates?.Invoke();screenPreview.InvalidateVisual();RefreshScreenReading();
            screenUndoCalibration.IsEnabled=screenCalibrationUndo.Count>0;
        }
        void SetScreenRegion(HudRegion region)
        {
            if(region==null||!region.Valid)throw new ArgumentException("Keep the region inside the image (0–100%).");
            RememberScreenCalibration();
            if(screenRegion.SelectedIndex==0)screenProfile.Health=region;
            else if(screenRegion.SelectedIndex==2)screenProfile.Shield=region;
            else {screenProfile.Badge=region;screenProfile.BadgeMask=null;}
            RefreshScreenCalibration();
            screenStatus.Text="Region changed · Save calibration to keep it";
        }
        Task ResetScreenCalibration()
        {
            var stopped=DisarmScreen();RememberScreenCalibration();
            screenProfile.Health=new ScreenProfile().Health;screenProfile.Badge=new ScreenProfile().Badge;screenProfile.Shield=new ScreenProfile().Shield;screenProfile.ReadShield=false;
            screenProfile.BadgeMask=null;screenProfile.Digits.Clear();
            SaveScreenProfile();RefreshScreenCalibration();screenStatus.Text="Calibration reset · Undo restores the previous setup";
            return stopped;
        }
        Task UndoScreenCalibration()
        {
            var stopped=DisarmScreen();
            if(screenCalibrationUndo.Count==0)return stopped;
            var previous=screenCalibrationUndo[screenCalibrationUndo.Count-1];screenCalibrationUndo.RemoveAt(screenCalibrationUndo.Count-1);
            // Feedback levels are independent settings, not part of calibration undo.
            screenProfile.Health=previous.Health;screenProfile.Badge=previous.Badge;
            screenProfile.Shield=previous.Shield;screenProfile.ReadShield=previous.ReadShield;
            screenProfile.BadgeMask=previous.BadgeMask;screenProfile.Digits=previous.Digits;
            RefreshScreenCalibration();screenStatus.Text="Calibration restored · Save calibration to keep it";
            return stopped;
        }
        void SaveScreenProfile(){if(!screenProfile.Validate())throw new ArgumentException("Invalid calibration.");Directory.CreateDirectory(Path.GetDirectoryName(ScreenProfilePath));var temp=ScreenProfilePath+".tmp";File.WriteAllText(temp,json.Serialize(screenProfile));File.Move(temp,ScreenProfilePath,true);}
        HudReading RefreshScreenReading()
        {
            if(screenFrame==null)return null;var read=HudVision.Read(screenFrame,screenProfile,screenGlyphs);
            screenReading.Text="Health "+(read.Health?.ToString()??"—")+" · "+Math.Round(read.Confidence*100)+"% match"+(screenProfile.ReadShield?"    Shield "+(read.Shield?.ToString()??"—")+" · "+Math.Round(read.ShieldConfidence*100)+"% match":"")+"    Badge "+(screenProfile.BadgeMask==null?"not calibrated":Math.Round(read.BadgeScore*100)+"% match");return read;
        }
        void RefreshScreenRoutes()
        {
            foreach(var d in devices.Values.Where(d=>d.Connected&&d.Player!=null))foreach(int channel in d.Motors.Keys) {
                string key=MixerKey(d,channel);if(screenRoutes.ContainsKey(key))continue;
                var saved=screenFeedback.Motors.TryGetValue(ScreenFeedbackKey(d,channel),out var preferences)?preferences:new ScreenMotorFeedback();
                var route=new ScreenRoute { Device=d,Channel=channel,Include=new CheckBox { Content=d.Name+(channel==0?"":" · Motor "+channel),IsChecked=!screenArmed&&saved.Enabled,MinWidth=150,Margin=new Thickness(0,0,16,0) },Ceiling=new Slider { Minimum=0,Maximum=20,Value=saved.Maximum,Width=130,TickFrequency=1,IsSnapToTickEnabled=true } };
                Accessible(route.Include,d.Name+" screen sync motor "+channel);Accessible(route.Ceiling,d.Name+" screen sync motor "+channel+" maximum level");
                var group=new StackPanel { Margin=new Thickness(0,8,0,12) };var row=new WrapPanel();row.Children.Add(route.Include);row.Children.Add(route.Ceiling);var value=Text(saved.Maximum+" / 20",13);value.Margin=new Thickness(12,0,0,0);row.Children.Add(value);group.Children.Add(row);
                var events=new WrapPanel { Margin=new Thickness(0,6,0,0) };
                foreach(string kind in new[]{"Damage","Kill","Death"}) {
                    var choice=new CheckBox { Content=kind,IsChecked=(saved.Events&ScreenMotorFeedback.EventMask(kind))!=0,Margin=new Thickness(0,0,20,6),MinHeight=28,VerticalContentAlignment=VerticalAlignment.Center };
                    Accessible(choice,d.Name+" screen sync motor "+channel+" "+kind.ToLowerInvariant()+" feedback");route.Events.Add(kind,choice);events.Children.Add(choice);
                    choice.Checked+=async(s,e)=>{var stopped=DisarmScreen();SaveScreenRoute(route);await stopped;};choice.Unchecked+=async(s,e)=>{var stopped=DisarmScreen();SaveScreenRoute(route);await stopped;};
                }
                group.Children.Add(events);screenMotorPanel.Children.Add(group);screenRoutes[key]=route;
                route.Include.Checked+=async(s,e)=>{var stopped=DisarmScreen();SaveScreenRoute(route);await stopped;};route.Include.Unchecked+=async(s,e)=>{var stopped=DisarmScreen();SaveScreenRoute(route);await stopped;};route.Ceiling.ValueChanged+=async(s,e)=>{value.Text=(int)route.Ceiling.Value+" / 20";var stopped=DisarmScreen();SaveScreenRoute(route);await stopped;};
            }
            foreach(var route in screenRoutes.Values)route.Include.IsEnabled=route.Device.Connected&&route.Device.Player!=null;
            screenNoMotors.Visibility=screenRoutes.Values.Any(r=>r.Device.Connected&&r.Device.Player!=null)?Visibility.Collapsed:Visibility.Visible;
        }
        async Task ArmScreen()
        {
            if(screenCapture==null||screenFrozen||screenFrame==null||ScreenCapture.Now-screenFrame.Time>.75)throw new ArgumentException("Start a live preview first.");
            var read=RefreshScreenReading();if(read?.Health==null||read.Health<=0)throw new ArgumentException("Calibrate health until the preview reads it correctly before enabling output.");
            var selected=screenRoutes.Values.Where(r=>r.Include.IsChecked==true&&r.Device.Connected&&r.Device.Player!=null&&r.Ceiling.Value>0&&ScreenRouteEvents(r)!=0).ToArray();if(selected.Length==0)throw new ArgumentException("Select a connected motor with an enabled event and a maximum above zero.");
            await ManualGuarded(async()=> {
                int operation=operatorGeneration,generation=screenGeneration;var capture=screenCapture;
                if(!await StopPlayers())throw new InvalidOperationException("Existing output could not be stopped.");
                if(closing||screenCapture==null||screenFrozen||capture!=screenCapture||operation!=operatorGeneration||generation!=screenGeneration||screenFrame==null||ScreenCapture.Now-screenFrame.Time>.75)return;
                // The HUD can change while another source or device is stopping.
                // Do not arm using the positive reading taken before those waits.
                var current=RefreshScreenReading();
                if(current?.Health==null||current.Health<=0){screenStatus.Text="HUD changed · verify health before enabling output";return;}
                screenOwned.Clear();foreach(var r in selected)if(r.Device.Connected&&r.Device.Player!=null)screenOwned.Add(new ScreenTarget { Device=r.Device,Channel=r.Channel,Generation=r.Device.Generation,Player=r.Device.Player,Ceiling=(int)r.Ceiling.Value,Events=ScreenRouteEvents(r) });
                if(screenOwned.Count==0)return;
                screenOperation=operatorGeneration;screenGeneration++;screenArmed=true;screenDeathUntil=0;screenArmedAt=ScreenCapture.Now;screenForegroundSeen=false;screenLastGood=ScreenCapture.Now;screenFilter.Reset();screenOutput.Content="Disable output";screenStatus.Text="Armed · return to the selected window within 8 seconds";
            },()=>screenGeneration);
        }
        async Task DisarmScreen()
        {
            bool wasArmed=screenArmed;
            screenArmed=false;screenGeneration++;
            if(screenOutput!=null)screenOutput.Content="Enable output";
            // Capture exact playback instances before awaiting; never stop a replacement device.
            var targets=screenOwned?.ToArray()??Array.Empty<ScreenTarget>();screenOwned?.Clear();
            if(screenStatus!=null&&(wasArmed||targets.Length>0))screenStatus.Text="Stopping output…";
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
            if(screenArmed||screenStatus==null||screenStopRetries.Any(a=>a.Pending))return;
            if(screenStopRetries.Any(a=>a.Current)){screenStatus.Text="Output stop unconfirmed · retrying";return;}
            // A newer calibration or capture message must survive a late stop.
            if(screenStatus.Text=="Stopping output…"||screenStatus.Text.StartsWith("Output stop unconfirmed",StringComparison.Ordinal))
                screenStatus.Text=screenCapture==null?"Capture off · output off":screenFrozen?"Frozen · output off":"Preview only · output off";
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
            var failure=Interlocked.Exchange(ref screenFailure,null);if(failure!=null){await StopScreenCapture();screenStatus.Text=failure;return;}
            var sample=Interlocked.Exchange(ref screenPending,null);var frame=sample!=null&&ReferenceEquals(sample.Source,screenCapture)?sample.Pixels:null;
            if(screenArmed) {
                if(screenDeathUntil>0&&now>=screenDeathUntil){await DisarmScreen();screenStatus.Text="Death detected · output off until enabled again";}
                if(screenOperation!=operatorGeneration||screenCapture==null||screenOwned.Any(t=>!t.Device.Connected||t.Device.Generation!=t.Generation||t.Device.Player!=t.Player||!t.Player.Active(t.Channel)&&t.Player.Level(t.Channel)<0)) {await DisarmScreen();screenStatus.Text="Source or device changed · output off";}
                else if(screenCapture.Foreground){if(!screenForegroundSeen){screenFilter.Reset();screenLastGood=now;}screenForegroundSeen=true;}
                else if(screenForegroundSeen||now-screenArmedAt>8){await DisarmScreen();screenStatus.Text="Window inactive · output off";}
                if(screenArmed&&screenForegroundSeen&&(screenFrame==null||now-screenFrame.Time>.75||screenLastGood>=0&&now-screenLastGood>1)) {await DisarmScreen();screenStatus.Text="HUD signal lost · output off";}
            }
            if(screenFrozen||frame==null)return;
            if(now-frame.Time>.5){screenFilter.Reset();return;}
            if(screenFrame!=null&&(screenFrame.Width!=frame.Width||screenFrame.Height!=frame.Height)){await DisarmScreen();screenFilter.Reset();screenStatus.Text="Window resized · verify calibration before enabling output";}
            screenFrame=frame;if(screenPreview.IsVisible)screenPreview.Show(frame);var read=RefreshScreenReading();if(read?.Health.HasValue==true)screenLastGood=now;
            var events=screenFilter.Push(read,frame.Time);
            foreach(var ev in events) {
                string description=ev.Kind=="Damage"?"Damage · −"+(ev.Amount-ev.ShieldAmount)+" HP"+(ev.ShieldAmount>0?" · −"+ev.ShieldAmount+" shield":""):ev.Kind;
                screenEvents.Enqueue(DateTime.Now.ToString("HH:mm:ss")+"  "+description+(screenArmed&&screenForegroundSeen?"":" · preview"));while(screenEvents.Count>8)screenEvents.Dequeue();screenHistory.Text=string.Join(Environment.NewLine,screenEvents.Reverse());
                if(screenArmed&&screenForegroundSeen&&screenDeathUntil==0&&!screenCueBusy&&(ev.Kind=="Death"||now-screenLastCue>=.45))await SendScreenCue(ev);
                if(ev.Kind=="Death"&&screenArmed)screenDeathUntil=now+.4;
            }
            if(screenArmed&&screenForegroundSeen)screenStatus.Text="Following HUD · output enabled";
        }
        async Task SendScreenCue(HudEvent ev)
        {
            int generation=screenGeneration;screenCueBusy=true;
            try {
                int high=ev.Kind=="Kill"?screenProfile.KillLevel:ev.Kind=="Death"?screenProfile.DeathLevel:Math.Max(1,(int)Math.Round(screenProfile.DamageLevel*(.35+.65*(100-ev.Health)/100.0)));
                if(ev.Kind=="Damage"&&screenProfile.DamageLevel==0)high=0;
                var clock=Stopwatch.StartNew();var starts=new List<Task>();
                foreach(var t in screenOwned.ToArray()) {
                    if((t.Events&ScreenMotorFeedback.EventMask(ev.Kind))==0)continue;
                    bool Current()=>screenArmed&&generation==screenGeneration&&operatorGeneration==screenOperation&&screenCapture?.Foreground==true&&t.Device.Connected&&t.Device.Generation==t.Generation&&ReferenceEquals(t.Device.Player,t.Player)&&screenFrame!=null&&ScreenCapture.Now-screenFrame.Time<.75;
                    if(!Current())break;
                    int level=Math.Min(t.Ceiling,high);if(level==0)continue;
                    var pattern=ev.Kind=="Kill"?new PatternSpec { mode="Custom",durationMs=450,periodMs=450,repeat=false,frames=new[]{new Frame { ms=0,level=Math.Max(1,level*3/4) },new Frame { ms=140,level=0 },new Frame { ms=250,level=level },new Frame { ms=450,level=0 }} }:new PatternSpec { mode="Steady",high=level,durationMs=ev.Kind=="Death"?350:220,transitionMs=40 };
                    if(starts.Count==0)screenLastCue=ScreenCapture.Now;
                    pattern.stopAtMs=pattern.durationMs;starts.Add(t.Player.Start(t.Channel,pattern,20,clock,Current));
                }
                await Task.WhenAll(starts);
            }catch{await DisarmScreen();screenStatus.Text="Device command failed · output off";}finally{screenCueBusy=false;}
        }
    }
}

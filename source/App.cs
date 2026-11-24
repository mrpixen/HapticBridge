using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;

namespace LovenseConnector
{
    internal sealed class MotorView
    {
        public int Channel;
        public Slider Level;
        public TextBox Duration;
        public TextBlock Output;
    }
    internal sealed class Device
    {
        public ulong Address;
        public string AdvertisedName, Name, Symbol, Firmware, Status="Available";
        public int? Battery;
        public bool Connected, Busy, Supported;
        public int Generation;
        public int[] Channels=new int[0];
        public BleLink Link;
        public Playback Player;
        public TabItem Tab;
        public StackPanel Content, Controls;
        public TextBlock State, Details;
        public Button ConnectButton;
        public CheckBox Light, AutoStop, AutoResume;
        public Dictionary<int,MotorView> Motors=new Dictionary<int,MotorView>();
    }
    internal sealed partial class ConnectorApp
    {
        readonly string root=AssetFiles.DataDirectory;
        readonly Dictionary<ulong,Device> devices=new Dictionary<ulong,Device>();
        readonly JavaScriptSerializer json=new JavaScriptSerializer();
        Window window;
        BleScanner scanner;
        Catalog catalog;
        TabControl tabs;
        TextBlock summary,hint,bluetooth;
        TextBox activity;
        Button scanButton,sweepButton;
        DispatcherTimer scanTimer,heartbeat;
        CancellationTokenSource sweep;
        bool scanning,closing,closeAllowed,ready,scanAutoConnect;
        readonly HashSet<ulong> scanAddresses=new HashSet<ulong>();
        int scanGeneration;
        DateTime scanStarted;
        string dataDir;
        int operatorGeneration;
        static string AppVersion=>typeof(ConnectorApp).Assembly.GetName().Version.ToString(3);
        T Find<T>(string name) where T:FrameworkElement { return (T)window.FindName(name); }
        static Brush Color(string value) { return (Brush)new BrushConverter().ConvertFromString(value); }
        static TextBlock Text(string value,int size=14) { return new TextBlock { Text=value,FontSize=size,TextWrapping=TextWrapping.Wrap,Margin=new Thickness(0,4,0,7) }; }
        static StackPanel Row() { return new StackPanel { Orientation=Orientation.Horizontal,Margin=new Thickness(0,3,0,5) }; }
        static TextBox Input(string value,int width=95) { return new TextBox { Text=value,Width=width,Margin=new Thickness(0,0,10,0),Padding=new Thickness(8,6,8,6),VerticalContentAlignment=VerticalAlignment.Center }; }
        static int Number(TextBox box) { int value; if(!int.TryParse(box.Text,out value)) throw new ArgumentException("Enter a whole number for " + (box.ToolTip??"this field") + "."); return value; }
        static void Field(Panel panel,string label,UIElement control) { var group=new StackPanel { Margin=new Thickness(0,0,12,8) }; group.Children.Add(Text(label,12)); group.Children.Add(control); panel.Children.Add(group); }
        Button Action(string title,Func<Task> action,Func<bool> canExecute=null)
        {
            var button=new Button { Content=title,Margin=new Thickness(0,0,8,6),Padding=new Thickness(14,8,14,8) };
            button.Click+=async(s,e)=> { button.IsEnabled=false; try { await action(); } catch(Exception ex) { Report(ex); } finally { button.IsEnabled=canExecute?.Invoke()??true; UpdateSummary(); } };
            return button;
        }
        void UI(Action action) { if(!window.Dispatcher.HasShutdownStarted) window.Dispatcher.BeginInvoke(action); }
        void Report(Exception ex) { Log("Error: " + ex.Message); hint.Text=ex.Message; }
        void CancelSweep() { if(sweep!=null) sweep.Cancel(); }
        Task PauseExternalSources()
        {
            // Each call invalidates its producer synchronously, before any BLE
            // stop is awaited. A slow device must not keep another source live.
            var audio=audioCapture!=null?StopAudioMotors():Task.CompletedTask;
            var video=DisarmFeelConnect();
            var local=RevokeIntegration(true);
            var screen=DisarmScreen();
            return Task.WhenAll(audio,video,local,screen);
        }
        Task Manual(Func<Task> action) => ManualGuarded(action,null);
        Task ManualDevice(Device d,Func<Playback,Func<bool>,Task> action)
        {
            int deviceGeneration=d.Generation;var player=d.Player;
            return Manual(()=> {
                int operation=operatorGeneration;
                bool Current()=>!closing&&operation==operatorGeneration&&deviceGeneration==d.Generation&&d.Connected&&player!=null&&ReferenceEquals(d.Player,player);
                return Current()?action(player,Current):Task.CompletedTask;
            });
        }
        async Task ManualGuarded(Func<Task> action,Func<int> requestRevision)
        {
            if(sweep!=null) { hint.Text="Stop the speed test before starting a manual output."; return; }
            int generation=++operatorGeneration;
            var stopped=Task.WhenAll(PauseExternalSources(),studioRunning?StopStudioMotors():Task.CompletedTask);
            // Capture after our synchronous source invalidation, before awaiting a slow stop.
            // A later cancellation of the requested source must also cancel this handoff.
            int revision=requestRevision?.Invoke()??0;
            await stopped;
            if(generation!=operatorGeneration||closing||sweep!=null||requestRevision!=null&&requestRevision()!=revision)return;
            await action();
        }
        async Task OperatorStop(Func<Task> action)
        {
            int generation=++operatorGeneration;CancelSweep();
            await Task.WhenAll(PauseExternalSources(),studioRunning?StopStudioMotors():Task.CompletedTask);
            if(generation!=operatorGeneration||closing)return;
            await action();
        }
        [STAThread]
        public static void Main(string[] args)
        {
            bool created;
            using(var mutex=new Mutex(true,"Local\\LovenseConnector.Desktop",out created))
            {
                if(!created) { MessageBox.Show("HapticBridge is already running.","HapticBridge"); return; }
                try { new ConnectorApp().Run(args); }
                catch(Exception ex) { MessageBox.Show("Could not start:\n"+ex.Message,"HapticBridge"); }
            }
        }
        void Run(string[] args)
        {
            dataDir=AssetFiles.DataDirectory; Directory.CreateDirectory(dataDir);
            File.WriteAllText(Path.Combine(dataDir,"session.log"),"HapticBridge "+AppVersion+" — "+DateTimeOffset.Now.ToString("o")+Environment.NewLine);
            catalog=Catalog.FromJson(AssetFiles.ReadText("models.json"));
            using(var stream=AssetFiles.Open("MainWindow.xaml")) window=(Window)XamlReader.Load(stream);
            _=new WindowFrame(window);
            tabs=Find<TabControl>("DeviceTabs"); summary=Find<TextBlock>("Summary"); hint=Find<TextBlock>("Hint"); bluetooth=Find<TextBlock>("BluetoothStatus"); activity=Find<TextBox>("ActivityLog");
            scanButton=Find<Button>("ScanButton"); sweepButton=Find<Button>("SweepButton");
            InitializeMixer();
            InitializeFeelConnect();
            InitializeScreenSync();
            InitializeTheme();
            InitializeStudioDevices();
            InitializeStudioSettings();
            InitializeStopShortcut();
            scanButton.Click+=async(s,e)=> { try { if(scanning) await EndScan(false); else StartScan(true); } catch(Exception ex) { Report(ex); } };
            Find<Button>("StopAllButton").Click+=async(s,e)=>await StopAll();
            Find<Button>("DisconnectAllButton").Click+=async(s,e)=> { await StopAll(); foreach(var d in devices.Values.ToArray()) await Disconnect(d); };
            sweepButton.Click+=async(s,e)=>await TestSweep();
            Find<Button>("HomeButton").Click+=(s,e)=> { tabs.SelectedItem=mixerTab;if(mixerTab.Content is ScrollViewer scroll)scroll.ScrollToTop(); };
            Find<Button>("DocsButton").Click+=(s,e)=>AssetFiles.ShowHelp(window);
            window.PreviewKeyDown+=async(s,e)=> { if(e.Key==Key.Escape) { e.Handled=true; await StopAll(); } };
            scanTimer=new DispatcherTimer { Interval=TimeSpan.FromSeconds(1) };
            scanTimer.Tick+=async(s,e)=> { if((DateTime.UtcNow-scanStarted).TotalSeconds>=10) await EndScan(scanAutoConnect); };
            heartbeat=new DispatcherTimer { Interval=TimeSpan.FromSeconds(2) }; heartbeat.Tick+=(s,e)=> { foreach(var d in devices.Values.Where(d=>d.Connected && d.Link!=null && !d.Link.Connected).ToArray()) Lost(d); SaveState(); }; heartbeat.Start();
            window.Loaded+=async(s,e)=> {
                try { InitializeApi(); scanner=new BleScanner(); scanner.Found+=(address,name,rssi)=>UI(()=>Found(address,name)); ready=await scanner.Available(); bluetooth.Text=ready?"Bluetooth ready":"Bluetooth unavailable"; scanButton.IsEnabled=ready; RefreshStudioDevices(); if(ready&&(args.Contains("--scan")||args.Contains("--connect-discovered"))) StartScan(args.Contains("--connect-discovered")); }
                catch(Exception ex) { Report(ex); }
            };
            window.Closing+=async(s,e)=> {
                if(closeAllowed) return;
                e.Cancel=true; if(closing) return; closing=true;CloseStopShortcut();StopAudio();if(mixerTimer!=null)mixerTimer.Stop(); scanTimer.Stop(); heartbeat.Stop(); if(scanner!=null) scanner.Stop(); if(api!=null) api.Dispose(); if(apiTimer!=null) apiTimer.Stop();
                FinishStudioDraftSaving();await StopAll();screenTimer?.Stop(); await CloseFeelConnect(); foreach(var d in devices.Values.ToArray()) Release(d,"Disconnected");
                if(scanner!=null) scanner.Dispose(); SaveState(); closeAllowed=true;
                _=window.Dispatcher.BeginInvoke(new Action(window.Close));
            };
            var app=new Application(); app.DispatcherUnhandledException+=(s,e)=> { Report(e.Exception); e.Handled=true; }; app.Run(window);
        }
        void Found(ulong address,string name)
        {
            if(scanning)scanAddresses.Add(address);
            if(closing||devices.ContainsKey(address)) return;
            var model=catalog.Match(name,""); var d=new Device { Address=address,AdvertisedName=name,Name=model.Name };
            devices.Add(address,d); BuildCard(d); Log("Found "+name); Update(d);
        }
        void BuildCard(Device d)
        {
            BuildStudioDeviceCard(d);
        }
        void BuildControls(Device d)
        {
            d.Controls.Children.Clear(); d.Motors.Clear();
            if(!d.Supported) { d.Controls.Children.Add(Text("This model is recognized by the catalog. Its actuator controls have not been enabled in this build.")); return; }
            var motors=new Grid(); int[] channels=d.Channels;
            for(int i=0;i<channels.Length;i++) motors.ColumnDefinitions.Add(new ColumnDefinition());
            for(int i=0;i<channels.Length;i++)
            {
                int channel=channels[i]; var v=new MotorView { Channel=channel }; d.Motors[channel]=v;
                var panel=new StackPanel(); var border=new Border { Background=Color("#9F241B35"),CornerRadius=new CornerRadius(20),Padding=new Thickness(18),Margin=new Thickness(i==0?0:8,10,i==0&&channels.Length>1?8:0,14),Child=panel }; Grid.SetColumn(border,i); motors.Children.Add(border);
                panel.Children.Add(Text(channel==0?"Vibration motor":"Motor "+channel,21)); var display=Text("3 / 20",17); panel.Children.Add(display);
                v.Level=new Slider { Minimum=0,Maximum=20,Value=3,TickFrequency=1,IsSnapToTickEnabled=true,SmallChange=1,LargeChange=1,TickPlacement=System.Windows.Controls.Primitives.TickPlacement.BottomRight,Margin=new Thickness(0,4,0,8) };
                System.Windows.Automation.AutomationProperties.SetName(v.Level,d.Name+" motor "+channel+" speed");
                v.Level.ValueChanged+=(s,e)=> { display.Text=""+(int)v.Level.Value+" / 20"; if(d.Player!=null&&sweep==null&&integration==null&&audioCapture==null&&!feelArmed&&!screenArmed&&!OwnsStudio) d.Player.LiveLevel(channel,(int)v.Level.Value); }; panel.Children.Add(v.Level);
                var nudges=Row(); nudges.Children.Add(Action("−1",()=> { v.Level.Value--; return Task.FromResult(0); })); nudges.Children.Add(Action("+1",()=> { v.Level.Value++; return Task.FromResult(0); })); panel.Children.Add(nudges);
                var time=Row(); v.Duration=Input("10000"); v.Duration.ToolTip="duration in milliseconds"; Field(time,"Duration (ms)",v.Duration); panel.Children.Add(time);
                var buttons=new WrapPanel();
                buttons.Children.Add(Action("Start",()=>ManualDevice(d,(player,current)=>player.Start(channel,new PatternSpec { mode="Steady",high=(int)v.Level.Value,durationMs=Number(v.Duration) },20,isCurrent:current))));
                buttons.Children.Add(Action("Test 2s",()=>ManualDevice(d,(player,current)=>player.Start(channel,new PatternSpec { mode="Steady",high=2,durationMs=2000 },20,isCurrent:current))));
                buttons.Children.Add(Action("Stop",()=>OperatorStop(()=>d.Player.Stop(channel)))); panel.Children.Add(buttons);
                v.Output=Text("Stopped",12); v.Output.Foreground=Color("#B6E6D1"); panel.Children.Add(v.Output);
            }
            d.Controls.Children.Add(new Expander { Header="Individual controls",Content=motors,Margin=new Thickness(0,16,0,16) });
            var actions=new WrapPanel(); actions.Children.Add(Action("Stop this toy",()=>OperatorStop(()=>d.Player.StopAll()))); actions.Children.Add(Action("Refresh battery",()=>ReadBattery(d))); actions.Children.Add(Action("Flash indicator",()=>d.Link.Command("Flash;"))); d.Controls.Children.Add(actions);
            BuildPatterns(d); BuildSettings(d);
        }
        void BuildPatterns(Device d)
        {
            var body=new StackPanel { Margin=new Thickness(4,10,4,12) }; d.Controls.Children.Add(new Expander { Header="Custom pattern",Content=body,Margin=new Thickness(0,14,0,12) });
            var row=new WrapPanel(); var target=new ComboBox { Width=145,Margin=new Thickness(0,0,10,0) };
            foreach(var v in d.Motors.Values) target.Items.Add(v.Channel==0?"Vibration motor":"Motor "+v.Channel);
            if(d.Motors.Count>1) target.Items.Add("All motors"); target.SelectedIndex=0;
            var mode=new ComboBox { Width=130 }; foreach(var m in new[]{"Steady","Pulse","Wave","Ramp","Custom"}) mode.Items.Add(m); mode.SelectedIndex=1;
            var name=Input("My pattern",190); Field(row,"Target",target); Field(row,"Shape",mode); Field(row,"Pattern name",name); body.Children.Add(row);
            var fields=new WrapPanel(); var low=Input("0"); var high=Input("3"); var period=Input("2000"); var duration=Input("10000"); var duty=Input("50"); var phase=Input("0");
            Field(fields,"Minimum 0–20",low); Field(fields,"Maximum 0–20",high); Field(fields,"Cycle ms",period); Field(fields,"Duration (ms)",duration); Field(fields,"Pulse on %",duty); Field(fields,"Phase °",phase); body.Children.Add(fields);
            var checks=Row(); var repeat=new CheckBox { Content="Repeat cycle",IsChecked=true,Margin=new Thickness(0,0,20,0) }; var interpolate=new CheckBox { Content="Interpolate custom points" }; checks.Children.Add(repeat); checks.Children.Add(interpolate); body.Children.Add(checks);
            body.Children.Add(Text("Points (ms:level)",12));
            var points=new TextBox { Text="0:0, 500:3, 1000:0, 1500:3, 2000:0",MinHeight=58,TextWrapping=TextWrapping.Wrap,AcceptsReturn=true,Padding=new Thickness(8) }; body.Children.Add(points);
            Func<PatternSpec> read=()=> {
                var spec=new PatternSpec { name=name.Text,mode=(string)mode.SelectedItem,low=Number(low),high=Number(high),periodMs=Number(period),durationMs=Number(duration),dutyPercent=Number(duty),phaseDegrees=Number(phase),repeat=repeat.IsChecked==true,interpolate=interpolate.IsChecked==true };
                if(spec.mode=="Custom") spec.frames=points.Text.Split(',').Select(x=> { var pair=x.Trim().Split(':'); int ms,level; if(pair.Length!=2||!int.TryParse(pair[0],out ms)||!int.TryParse(pair[1],out level)) throw new ArgumentException("Custom points use ms:level, for example 0:0, 500:3."); return new Frame { ms=ms,level=level }; }).ToArray();
                spec.Validate(); return spec;
            };
            var actions=new WrapPanel { Margin=new Thickness(0,10,0,0) };
            actions.Children.Add(Action("Play pattern",()=>ManualDevice(d,async(player,current)=> {
                var spec=read(); var selected=d.Motors.Values.ToArray(); if(target.SelectedIndex<selected.Length) selected=new[]{selected[target.SelectedIndex]};
                foreach(var motor in selected) {
                    if(!current())return;
                    await player.Start(motor.Channel,json.Deserialize<PatternSpec>(json.Serialize(spec)),20,isCurrent:current);
                }
            })));
            actions.Children.Add(Action("Save JSON…",async()=> { var spec=read(); if(!await StopAll())return; var dialog=new SaveFileDialog { Filter="Pattern JSON|*.json",FileName="pattern.json",InitialDirectory=AssetFiles.PatternDirectory }; Directory.CreateDirectory(dialog.InitialDirectory); if(dialog.ShowDialog(window)==true) File.WriteAllText(dialog.FileName,json.Serialize(spec)); }));
            actions.Children.Add(Action("Load JSON…",async()=> {
                if(!await StopAll())return;
                var dialog=new OpenFileDialog { Filter="Pattern JSON|*.json",InitialDirectory=AssetFiles.PatternDirectory };
                if(dialog.ShowDialog(window)==true) {
                    if(new FileInfo(dialog.FileName).Length>131072) throw new ArgumentException("Pattern file is too large.");
                    var spec=json.Deserialize<PatternSpec>(File.ReadAllText(dialog.FileName)); if(spec==null) throw new ArgumentException("Pattern is empty."); spec.Validate();
                    name.Text=spec.name; mode.SelectedItem=spec.mode; low.Text=spec.low.ToString(); high.Text=spec.high.ToString(); period.Text=spec.periodMs.ToString(); duration.Text=spec.durationMs.ToString(); duty.Text=spec.dutyPercent.ToString(); phase.Text=spec.phaseDegrees.ToString(); repeat.IsChecked=spec.repeat; interpolate.IsChecked=spec.interpolate;
                    points.Text=String.Join(", ",(spec.frames??new Frame[0]).Select(f=>f.ms+":"+f.level));
                }
            })); body.Children.Add(actions);
            var presetBody=new StackPanel { Margin=new Thickness(4,10,4,12) };
            var presetRow=new WrapPanel(); var slot=new ComboBox { Width=80 }; for(int i=1;i<=10;i++) slot.Items.Add(i); slot.SelectedIndex=0; var presetTime=Input("2000"); Field(presetRow,"Slot",slot); Field(presetRow,"Duration ms · 500–60000",presetTime);
            presetRow.Children.Add(Action("Play hardware preset",()=>ManualDevice(d,(player,current)=>player.HardwarePreset((int)slot.SelectedItem,Number(presetTime))))); presetBody.Children.Add(presetRow);
            d.Controls.Children.Add(new Expander { Header="Device presets",Content=presetBody,Margin=new Thickness(0,8,0,16) });
        }
        void BuildSettings(Device d)
        {
            var panel=new StackPanel { Margin=new Thickness(4,10,4,12) };
            d.Light=new CheckBox { Content="Indicator light enabled",Margin=new Thickness(0,4,0,10) };
            d.AutoStop=new CheckBox { Content="Stop output if Bluetooth disconnects",Margin=new Thickness(0,4,0,10) };
            d.AutoResume=new CheckBox { Content="Resume last output when Bluetooth reconnects",Margin=new Thickness(0,4,0,10) };
            panel.Children.Add(d.Light); panel.Children.Add(d.AutoStop); panel.Children.Add(d.AutoResume);
            var row=new WrapPanel(); row.Children.Add(Action("Read settings",()=>ReadSettings(d)));
            row.Children.Add(Action("Apply settings",async()=> {
                await d.Link.Command("Light:"+(d.Light.IsChecked==true?"on":"off")+";");
                await d.Link.Command("AutoSwith:"+(d.AutoStop.IsChecked==true?"On":"Off")+":"+(d.AutoResume.IsChecked==true?"On":"Off")+";"); await ReadSettings(d);
            }));
            row.Children.Add(Action("Power off toy",()=>PowerOff(d))); panel.Children.Add(row);
            d.Controls.Children.Add(new Expander { Header="Settings",Content=panel,Margin=new Thickness(0,8,0,12) });
        }
        void StartScan(bool connectDiscovered=false)
        {
            if(!ready||scanning||closing) return;
            scanner.Start();scanGeneration++;scanAddresses.Clear();scanAutoConnect=connectDiscovered;
            scanning=true;scanStarted=DateTime.UtcNow;scanTimer.Start();scanButton.Content="Stop scanning";
            if(studioDevicesTab!=null)tabs.SelectedItem=studioDevicesTab;
            hint.Text=connectDiscovered?"Searching for 10 seconds, then connecting…":"Searching for nearby devices for 10 seconds…";
            Log("Scan started.");UpdateSummary();
        }
        async Task EndScan(bool connect)
        {
            if(!scanning) return;
            scanTimer.Stop();scanner.Stop();scanning=false;scanAutoConnect=false;scanButton.Content="Find & connect";
            int generation=scanGeneration;
            var found=devices.Values.Where(d=>scanAddresses.Contains(d.Address)).ToArray();
            hint.Text=found.Length==0?"No nearby devices found. Turn one on and try again.":connect?"Connecting discovered devices…":"Choose Connect beside a device.";
            UpdateSummary();
            if(connect)foreach(var d in found) {
                if(closing||generation!=scanGeneration)break;
                if(!d.Connected)await Connect(d);
            }
            if(connect&&!closing&&generation==scanGeneration&&found.Length>0&&found.All(d=>d.Connected))hint.Text="Connected · ready";
        }
        async Task Connect(Device d)
        {
            if(d.Busy||d.Connected||closing) return; d.Busy=true; d.Status="Connecting…"; int generation=++d.Generation; Update(d); BleLink link=null;
            try
            {
                link=await BleLink.Open(d.Address); if(closing||generation!=d.Generation) { link.Dispose(); return; }
                d.Link=link; link.Trace+=line=>UI(()=>Log(d.Name+": "+line)); link.Lost+=()=>UI(()=> { if(Object.ReferenceEquals(d.Link,link)) Lost(d); });
                string type=await link.Query("DeviceType;",value=>value.Count(c=>c==':')>=2);
                if(closing||generation!=d.Generation||!Object.ReferenceEquals(d.Link,link)) { link.Dispose(); return; }
                var parts=type.Split(':'); d.Symbol=parts[0].ToUpperInvariant(); d.Firmware=parts[1];
                var model=catalog.Match(d.AdvertisedName,d.Symbol); d.Name=model.Name;
                d.Channels=Catalog.VibrationChannels(model); d.Supported=d.Channels.Length>0;
                link.EdgeMotorEcho=d.Symbol=="P"&&d.Firmware=="204";
                d.Player=d.Supported?new Playback(link,d.Channels):null; BuildControls(d);
                if(d.Player!=null)
                {
                    var player=d.Player;
                    player.Changed+=(channel,level,state)=>UI(()=> { if(Object.ReferenceEquals(d.Player,player) && d.Motors.ContainsKey(channel)) { d.Motors[channel].Output.Text=(level<0?"Unconfirmed":level+" / 20")+" · "+state; SaveState(); } });
                    player.Error+=message=>UI(()=> { if(!Object.ReferenceEquals(d.Player,player)) return; hint.Text=d.Name+": "+message; Log(hint.Text); if(!link.Connected) Lost(d); }); await player.StopAll();
                }
                if(closing||generation!=d.Generation||!Object.ReferenceEquals(d.Link,link)) { link.Dispose(); return; }
                await ReadBattery(d); if(d.Supported&&generation==d.Generation) { try { await ReadSettings(d); } catch { Log("Device settings are not supported by this firmware."); } }
                if(closing||generation!=d.Generation||!Object.ReferenceEquals(d.Link,link)) { link.Dispose(); return; }
                if(!link.Connected) throw new InvalidOperationException("Toy disconnected during setup.");
                d.Connected=true; d.Status="Connected · ready"; hint.Text="Connected · ready"; Log(d.Name+" connected. Firmware "+d.Firmware+".");
            }
            catch(Exception ex) { if(link!=null) link.Dispose(); if(generation==d.Generation) { Release(d,"Connection failed · retry"); Report(ex); } }
            finally { if(generation==d.Generation) { d.Busy=false; Update(d); } }
        }
        async Task ReadBattery(Device d)
        {
            var link=d.Link; if(link==null) return;
            string value=await link.Query("Battery;",x=> { int n; return int.TryParse(x,out n)&&n>=0&&n<=100; });
            if(Object.ReferenceEquals(d.Link,link)) { d.Battery=int.Parse(value); Update(d); }
        }
        async Task ReadSettings(Device d)
        {
            var link=d.Link; if(link==null) return;
            string light=await link.Query("GetLight;",x=>x=="Light:1"||x=="Light:0");
            string switches=await link.Query("GetAS;",x=>System.Text.RegularExpressions.Regex.IsMatch(x,@"^AutoSwith:[01]:[01]$")); var fields=switches.Split(':');
            if(Object.ReferenceEquals(d.Link,link)) { d.Light.IsChecked=light=="Light:1"; d.AutoStop.IsChecked=fields[1]=="1"; d.AutoResume.IsChecked=fields[2]=="1"; }
        }
        async Task Disconnect(Device d)
        {
            operatorGeneration++;int deviceGeneration=d.Generation;CancelSweep();
            await Task.WhenAll(PauseExternalSources(),studioRunning?StopStudioMotors():Task.CompletedTask);
            if(deviceGeneration!=d.Generation||closing)return;
            if(d.Player!=null) try { await d.Player.StopAll(); } catch(Exception ex) { Report(ex); }
            // Stop all cancels output, not an already requested disconnect. A new
            // connection generation, however, must never be released by this request.
            if(deviceGeneration==d.Generation&&!closing)Release(d,"Disconnected");
        }
        Task PowerOff(Device d)
        {
            int generation=d.Generation;var player=d.Player;var link=d.Link;
            bool Current()=>!closing&&generation==d.Generation&&d.Connected&&player!=null&&ReferenceEquals(d.Player,player)&&ReferenceEquals(d.Link,link);
            return OperatorStop(async()=> {
                if(!Current())return;
                await player.StopAll();
                if(!Current())return;
                try { await link.Command("PowerOff;"); }
                finally {
                    // A connection can be replaced while the command is pending.
                    // Only the original connection belongs to this request.
                    if(Current())Release(d,"Power off requested · turn on to reconnect");
                }
            });
        }
        void Lost(Device d)
        {
            if(!d.Connected&&!d.Busy) return; if(sweep!=null) sweep.Cancel(); Release(d,"Connection lost · output unconfirmed"); Log(d.Name+": connection lost; playback canceled.");
        }
        void Release(Device d,string state)
        {
            d.Generation++; if(d.Player!=null) d.Player.CancelAll(); d.Player=null; if(d.Link!=null) d.Link.Dispose(); d.Link=null; d.Connected=false; d.Busy=false; d.Status=state; d.Battery=null;
            foreach(var motor in d.Motors.Values) motor.Output.Text="Disconnected · current output unconfirmed";
            Update(d);
        }
        async Task<bool> StopAll()
        {
            operatorGeneration++;
            var capture=StopScreenCapture();
            StopStudioPreview();
            ClearStudioOwnership();
            CancelSweep();
            var sources=PauseExternalSources();var stops=StopPlayers();
            bool success;
            try { await Task.WhenAll(sources,stops,capture);success=await stops; }
            catch(Exception ex) { Report(ex);success=false; }
            hint.Text=!devices.Values.Any(d=>d.Connected)?"No active toy connections. Disconnected output cannot be confirmed.":success?"All connected motors stopped.":"Stop could not be confirmed for every toy. Check connection status.";SaveState();return success;
        }
        async Task<bool> StopPlayers()
        {
            var active=devices.Values.Where(d=>d.Player!=null).Select(d=>new { Device=d,Player=d.Player }).ToArray();
            var results=await Task.WhenAll(active.Select(async item=> { try { await item.Player.StopAll(); return true; } catch(Exception ex) { Report(ex); return false; } }));
            return results.All(ok=>ok);
        }
        async Task TestSweep()
        {
            if(sweep!=null||closing) return;
            var testDevices=devices.Values.Where(d=>d.Connected&&d.Supported).OrderBy(d=>d.Name).ToArray();
            if(testDevices.Length==0) { hint.Text="Connect a supported toy first."; return; }
            int generation=++operatorGeneration;
            var cancel=new CancellationTokenSource();sweep=cancel;sweepButton.IsEnabled=false;
            var testMotors=testDevices.SelectMany(d=>d.Motors.Values).ToArray();
            foreach(var motor in testMotors) motor.Level.IsEnabled=false;
            try
            {
                var sources=PauseExternalSources();var stops=StopPlayers();
                await Task.WhenAll(sources,stops);
                if(generation!=operatorGeneration||closing)cancel.Cancel();
                if(!await stops) throw new InvalidOperationException("Speed test canceled because the initial stop was not confirmed.");
                cancel.Token.ThrowIfCancellationRequested();
                foreach(var d in testDevices)
                {
                    cancel.Token.ThrowIfCancellationRequested();
                    await d.Link.Command("AutoSwith:On:Off;"); await ReadSettings(d);
                    foreach(var motor in d.Motors.Values.OrderBy(m=>m.Channel))
                    {
                        cancel.Token.ThrowIfCancellationRequested(); SelectStudioDevice(d);
                        hint.Text="Speed test: "+d.Name+" / motor "+motor.Channel+" · levels 1–20 · 1 second each"; Log(hint.Text); var player=d.Player;
                        for(int level=1;level<=20;level++)
                        {
                            cancel.Token.ThrowIfCancellationRequested(); motor.Level.Value=level;
                            await player.Start(motor.Channel,new PatternSpec { mode="Steady",high=level,durationMs=5000 },20);
                            var deadline=DateTime.UtcNow.AddSeconds(3);
                            while(player.Level(motor.Channel)!=level) { cancel.Token.ThrowIfCancellationRequested(); if(DateTime.UtcNow>deadline) throw new TimeoutException("Test level was not acknowledged."); await Task.Delay(25,cancel.Token); }
                            await Task.Delay(1000,cancel.Token);
                        }
                        await player.Stop(motor.Channel); Log(d.Name+" motor "+motor.Channel+": all levels 1–20 acknowledged; Stop acknowledged."); await Task.Delay(2000,cancel.Token);
                    }
                }
                hint.Text="Speed test complete. All tested motor channels are stopped."; Log("Full speed test completed.");
            }
            catch(OperationCanceledException) { hint.Text="Speed test canceled."; Log("Speed test canceled."); }
            catch(Exception ex) { Report(ex); }
            // All test exceptions are handled above; always finish with acknowledged stops.
            {
                foreach(var d in testDevices) if(d.Player!=null) try { await d.Player.StopAll(); } catch(Exception ex) { Report(ex); }
                foreach(var motor in testMotors) { motor.Level.Value=3; motor.Level.IsEnabled=true; }
                sweep=null; cancel.Dispose(); UpdateSummary(); SaveState();
            }
        }
        void Update(Device d)
        {
            d.Tab.Header=d.Name; d.State.Text=d.Name+" · "+d.Status;
            d.Details.Text=d.AdvertisedName+"   ·   Battery "+(d.Battery.HasValue?d.Battery+"%":"—")+(d.Firmware!=null?"   ·   Firmware "+d.Firmware:"");
            d.ConnectButton.Content=d.Connected?"Disconnect":d.Busy?"Connecting…":"Connect"; d.ConnectButton.IsEnabled=!d.Busy&&!closing; d.Controls.IsEnabled=d.Connected&&!closing; UpdateSummary();
        }
        void UpdateSummary()
        {
            summary.Text=devices.Values.Count(d=>d.Connected)+" connected · "+devices.Count+" discovered";
            sweepButton.IsEnabled=sweep==null&&!closing&&devices.Values.Any(d=>d.Connected&&d.Supported);
            Find<TextBlock>("EmptyState").Visibility=Visibility.Collapsed;RefreshMixer(); RefreshFeelRoutes(); RefreshStudioDevices(); RefreshStudioDock(); SaveState();
        }
        void Log(string message)
        {
            string line=DateTime.Now.ToString("HH:mm:ss")+"  "+message;
            if(activity.Text.Length>24000) activity.Text=activity.Text.Substring(activity.Text.Length-12000); activity.AppendText(line+Environment.NewLine); activity.ScrollToEnd();
            try { var path=Path.Combine(dataDir,"session.log"); if(new FileInfo(path).Length>1048576) File.WriteAllText(path,""); File.AppendAllText(path,line+Environment.NewLine); } catch { }
        }
        void SaveState()
        {
            if(dataDir==null) return;
            try
            {
                var state=new { version=AppVersion,running=!closing,processId=Process.GetCurrentProcess().Id,updatedUtc=DateTime.UtcNow.ToString("o"),scanning=scanning,testing=sweep!=null,
                    devices=devices.Values.Select(d=>new { name=d.Name,advertisedName=d.AdvertisedName,connected=d.Connected,state=d.Status,battery=d.Battery,firmware=d.Firmware,controlsEnabled=d.Supported,
                        motors=d.Motors.Values.Select(m=>new { channel=m.Channel,acknowledgedLevel=d.Player==null?-1:d.Player.Level(m.Channel),active=d.Player!=null&&d.Player.Active(m.Channel) }).ToArray() }).ToArray() };
                var path=Path.Combine(dataDir,"status.json"); var temp=Path.Combine(dataDir,"status.tmp"); File.WriteAllText(temp,json.Serialize(state)); if(File.Exists(path)) File.Replace(temp,path,null); else File.Move(temp,path);
            }
            catch { }
        }
    }
}

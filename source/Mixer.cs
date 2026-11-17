using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using Microsoft.Win32;

namespace LovenseConnector
{
    internal sealed class MixerMotor
    {
        public Device Device; public int Channel; public CheckBox Include; public Slider Level; public TextBlock Status; public ProgressBar Meter; public Border Card;
    }
    public sealed class MixerPreset
    {
        public int version=1;
        public string shape="Wave";
        public int low=0,high=12,periodMs=2400,durationMs=10000,transitionMs=200;
        public bool stagger;
        public Dictionary<string,int> levels=new Dictionary<string,int>();
    }
    internal sealed partial class ConnectorApp
    {
        TabItem mixerTab; StackPanel mixerBody; WrapPanel mixerMotors;
        readonly Dictionary<string,MixerMotor> mixerRows=new Dictionary<string,MixerMotor>();
        ComboBox mixerShape,audioMode; Slider mixerHigh,audioGain; TextBox mixerLow,mixerPeriod,mixerDuration,mixerTransition;
        CheckBox mixerStagger; TextBlock mixerLabel,audioLabel; Polyline patternLine;
        AudioSync audioCapture; DispatcherTimer mixerTimer; bool audioTickBusy,mixerLoading;
        MixerMotor[] audioTargets; int audioGeneration;
        static string MixerKey(Device d,int c) { return d.Address.ToString("X12")+"/"+c; }
        void InitializeMixer()
        {
            mixerBody=new StackPanel { Margin=new Thickness(24) };
            mixerTab=new TabItem { Header="Controls",Content=new ScrollViewer { Content=mixerBody,VerticalScrollBarVisibility=ScrollBarVisibility.Auto,HorizontalScrollBarVisibility=ScrollBarVisibility.Disabled } };
            tabs.Items.Insert(0,mixerTab);tabs.SelectedItem=mixerTab;
            mixerBody.Children.Add(Text("Controls",32));
            mixerLabel=Text("Find a device to add its motors.",13);mixerLabel.Foreground=Color("#C4BAD7");mixerLabel.Margin=new Thickness(0,4,0,18);mixerBody.Children.Add(mixerLabel);
            mixerMotors=new WrapPanel();mixerBody.Children.Add(mixerMotors);
            var actions=new WrapPanel { Margin=new Thickness(0,8,0,18) };
            actions.Children.Add(Action("Start selected",()=>Manual(StartMixerSteady)));
            actions.Children.Add(Action("Stop selected",()=>OperatorStop(async()=> { var targets=SelectedMixer();await Task.WhenAll(targets.Select(m=>m.Device.Player.Stop(m.Channel))); })));
            actions.Children.Add(Action("Select all",()=> { foreach(var m in mixerRows.Values)m.Include.IsChecked=true;return Task.FromResult(0); }));mixerBody.Children.Add(actions);
            var linked=new DockPanel();var linkedValue=Text("12 / 20",17);DockPanel.SetDock(linkedValue,Dock.Right);linked.Children.Add(linkedValue);linked.Children.Add(Text("Linked intensity",18));mixerBody.Children.Add(linked);
            mixerHigh=new Slider { Minimum=0,Maximum=20,Value=12,TickFrequency=1,IsSnapToTickEnabled=true,Margin=new Thickness(0,0,0,18) };
            System.Windows.Automation.AutomationProperties.SetName(mixerHigh,"Linked intensity");mixerBody.Children.Add(mixerHigh);
            mixerHigh.ValueChanged+=(s,e)=> { linkedValue.Text=(int)mixerHigh.Value+" / 20";foreach(var m in mixerRows.Values.Where(m=>m.Include.IsChecked==true))m.Level.Value=mixerHigh.Value;UpdateMixerPreview(); };
            mixerLow=Input("0");mixerPeriod=Input("2400");mixerDuration=Input("0");mixerTransition=Input("200");
            mixerDuration.ToolTip="0 keeps playing until stopped";
            var timing=new WrapPanel { Margin=new Thickness(0,10,0,8) };
            Field(timing,"Minimum",mixerLow);Field(timing,"Cycle (ms)",mixerPeriod);Field(timing,"Duration (ms)",mixerDuration);Field(timing,"Smoothing (ms)",mixerTransition);
            var patterns=new WrapPanel { Margin=new Thickness(0,8,0,6) };
            mixerShape=new ComboBox { Width=155,Margin=new Thickness(0,0,12,8) };
            foreach(var name in new[]{"Steady","Wave","Pulse","Heartbeat","Surge","Staircase"})mixerShape.Items.Add(name);mixerShape.SelectedItem="Wave";
            System.Windows.Automation.AutomationProperties.SetName(mixerShape,"Pattern");
            mixerStagger=new CheckBox { Content="Ripple",Margin=new Thickness(0,0,18,8),ToolTip="Offset each motor through the pattern" };
            patterns.Children.Add(mixerShape);patterns.Children.Add(Action("Play pattern",()=>Manual(PlayMixerPattern)));patterns.Children.Add(mixerStagger);
            patterns.Children.Add(Action("Save…",SaveMixer));patterns.Children.Add(Action("Load…",()=>Manual(LoadMixer)));mixerBody.Children.Add(patterns);
            var preview=new Canvas { Height=58,Background=Color("#500A0712"),ClipToBounds=true,Margin=new Thickness(0,8,0,10) };
            patternLine=new Polyline { Stroke=Color("#CCB8FF"),StrokeThickness=2 };preview.Children.Add(patternLine);preview.SizeChanged+=(s,e)=>UpdateMixerPreview();mixerBody.Children.Add(preview);
            mixerBody.Children.Add(new Expander { Header="Timing",Content=timing,Margin=new Thickness(0,8,0,0) });
            mixerShape.SelectionChanged+=(s,e)=>UpdateMixerPreview();foreach(var box in new[]{mixerLow,mixerPeriod,mixerDuration,mixerTransition})box.TextChanged+=(s,e)=>UpdateMixerPreview();
            var soundBody=new StackPanel { Margin=new Thickness(24) };soundBody.Children.Add(Text("Sound",32));
            soundBody.Children.Add(Text("Use the selected motors from Controls.",13));
            var sound=new WrapPanel { Margin=new Thickness(0,22,0,18) };audioMode=new ComboBox { Width=190 };audioMode.Items.Add("Bass rumble");audioMode.Items.Add("Full sound");audioMode.SelectedIndex=0;
            audioGain=new Slider { Minimum=1,Maximum=12,Value=4,Width=220,TickFrequency=1,IsSnapToTickEnabled=true };
            System.Windows.Automation.AutomationProperties.SetName(audioGain,"Sound sensitivity");
            Field(sound,"Response",audioMode);Field(sound,"Sensitivity",audioGain);soundBody.Children.Add(sound);
            var soundActions=new WrapPanel();soundActions.Children.Add(Action("Start listening",()=>Manual(StartAudio)));soundActions.Children.Add(Action("Stop",()=>OperatorStop(StopAudioMotors)));soundBody.Children.Add(soundActions);
            audioLabel=Text("Stopped",14);audioLabel.Margin=new Thickness(0,20,0,0);soundBody.Children.Add(audioLabel);
            tabs.Items.Insert(1,new TabItem { Header="Sound",Content=new ScrollViewer { Content=soundBody,VerticalScrollBarVisibility=ScrollBarVisibility.Auto } });
            mixerTimer=new DispatcherTimer { Interval=TimeSpan.FromMilliseconds(100) };mixerTimer.Tick+=async(s,e)=> { RefreshMixerMeters();await AudioTick(); };mixerTimer.Start();
            UpdateMixerPreview();
        }
        MixerMotor[] SelectedMixer() { return mixerRows.Values.Where(m=>m.Include.IsChecked==true&&m.Device.Connected&&m.Device.Player!=null).ToArray(); }
        void RefreshMixer()
        {
            if(mixerMotors==null)return;
            foreach(var d in devices.Values)foreach(var channel in d.Motors.Keys) {
                string key=MixerKey(d,channel);if(mixerRows.ContainsKey(key))continue;
                var m=new MixerMotor { Device=d,Channel=channel };var panel=new StackPanel();
                m.Card=new Border { Width=226,CornerRadius=new CornerRadius(20),Background=Color("#A0241935"),Padding=new Thickness(18),Margin=new Thickness(0,6,12,12),Child=panel };
                var heading=new StackPanel { MaxWidth=152 };
                heading.Children.Add(new TextBlock { Text=d.Name,TextWrapping=TextWrapping.Wrap,FontSize=16 });
                heading.Children.Add(new TextBlock { Text=channel==0?"Motor":"Motor "+channel,FontSize=12,Margin=new Thickness(0,4,0,0),Foreground=Color("#C4BAD7") });
                m.Include=new CheckBox { Content=heading,IsChecked=true,FontSize=16,Margin=new Thickness(0,0,0,10) };panel.Children.Add(m.Include);
                System.Windows.Automation.AutomationProperties.SetName(m.Include,d.Name+" motor "+channel+" selected");
                var label=Text("12 / 20",14);panel.Children.Add(label);
                m.Level=new Slider { Minimum=0,Maximum=20,Value=12,TickFrequency=1,IsSnapToTickEnabled=true,SmallChange=1,LargeChange=1 };
                System.Windows.Automation.AutomationProperties.SetName(m.Level,d.Name+" mixer motor "+channel+" level");
                m.Level.ValueChanged+=(s,e)=> { label.Text=""+(int)m.Level.Value+" / 20";if(!mixerLoading&&integration==null&&audioCapture==null&&!feelArmed&&sweep==null&&m.Device.Player!=null)m.Device.Player.LiveLevel(channel,(int)m.Level.Value); };
                m.Include.Unchecked+=async(s,e)=> { if(!mixerLoading&&integration==null&&!feelArmed&&sweep==null&&m.Device.Player!=null)try { await m.Device.Player.Stop(channel); }catch(Exception ex) { Report(ex); } };
                panel.Children.Add(m.Level);var buttons=new WrapPanel { Margin=new Thickness(0,8,0,0) };
                buttons.Children.Add(Action("On",()=>Manual(()=>m.Device.Player.Start(channel,new PatternSpec { mode="Steady",high=(int)m.Level.Value,durationMs=Number(mixerDuration),transitionMs=Number(mixerTransition) },20))));
                buttons.Children.Add(Action("Off",()=>OperatorStop(()=>m.Device.Player.Stop(channel))));panel.Children.Add(buttons);
                m.Meter=new ProgressBar { Minimum=0,Maximum=20,Height=7,Foreground=Color("#B6E6D1"),Background=Color("#392A46"),BorderThickness=new Thickness(0),Margin=new Thickness(0,8,0,4) };panel.Children.Add(m.Meter);
                m.Status=Text("Stopped",12);panel.Children.Add(m.Status);mixerRows.Add(key,m);mixerMotors.Children.Add(m.Card);
            }
            foreach(var m in mixerRows.Values)m.Card.IsEnabled=m.Device.Connected&&sweep==null&&!closing;
        }
        void RefreshMixerMeters() { foreach(var m in mixerRows.Values) { int value=m.Device.Player==null?-1:m.Device.Player.Level(m.Channel);m.Meter.Value=Math.Max(0,value);m.Status.Text=!m.Device.Connected?"Disconnected":value<0?"Output unconfirmed":value==0?"Stopped":"Playing · "+value+" / 20"; } }
        PatternSpec MixerPattern(int phase)
        {
            int high=(int)mixerHigh.Value,low=Number(mixerLow),period=Number(mixerPeriod);string shape=(string)mixerShape.SelectedItem;
            var p=new PatternSpec { name=shape,mode=shape,low=low,high=high,periodMs=period,durationMs=Number(mixerDuration),transitionMs=Number(mixerTransition),phaseDegrees=phase };
            if(shape=="Heartbeat"||shape=="Surge"||shape=="Staircase") {
                p.mode="Custom";p.interpolate=shape!="Heartbeat";
                double[] times=shape=="Heartbeat"?new[]{0.0,.14,.28,.42,.58,1.0}:shape=="Surge"?new[]{0.0,.15,.6,.78,1.0}:new[]{0.0,.25,.5,.75,1.0};
                double[] levels=shape=="Heartbeat"?new[]{0.0,1.0,0.0,.8,0.0,0.0}:shape=="Surge"?new[]{0.0,.2,1.0,1.0,0.0}:new[]{0.0,.25,.5,1.0,0.0};
                p.frames=times.Select((t,i)=>new Frame { ms=(int)(t*period),level=low+(int)Math.Round((high-low)*levels[i]) }).ToArray();
            }
            p.Validate();return p;
        }
        async Task StartMixerSteady()
        {
            var targets=SelectedMixer();if(targets.Length==0)throw new InvalidOperationException("Connect and select at least one motor.");
            int duration=Number(mixerDuration),transition=Number(mixerTransition);new PatternSpec { durationMs=duration,transitionMs=transition }.Validate();
            var clock=Stopwatch.StartNew();await Task.WhenAll(targets.Select(m=>m.Device.Player.Start(m.Channel,new PatternSpec { mode="Steady",high=(int)m.Level.Value,durationMs=duration,transitionMs=transition },20,clock)));
            mixerLabel.Text="Independent levels · "+targets.Length+" motors playing";
        }
        async Task PlayMixerPattern()
        {
            var targets=SelectedMixer();if(targets.Length==0)throw new InvalidOperationException("Connect and select at least one motor.");
            var specs=targets.Select((m,i)=> { var p=MixerPattern(mixerStagger.IsChecked==true?i*360/targets.Length:0);double ratio=mixerHigh.Value==0?0:m.Level.Value/mixerHigh.Value; if(mixerHigh.Value==0&&p.frames!=null) { var high=(int)m.Level.Value; var shape=(string)mixerShape.SelectedItem; double[] normalized=shape=="Heartbeat"?new[]{0.0,1.0,0.0,.8,0.0,0.0}:shape=="Surge"?new[]{0.0,.2,1.0,1.0,0.0}:new[]{0.0,.25,.5,1.0,0.0}; for(int k=0;k<p.frames.Length;k++)p.frames[k].level=(int)Math.Round(high*normalized[k]); ratio=1; }p.high=(int)m.Level.Value;p.low=Math.Min(p.high,(int)Math.Round(p.low*ratio));if(p.frames!=null)foreach(var frame in p.frames)frame.level=Math.Min(20,(int)Math.Round(frame.level*ratio));p.Validate();return p; }).ToArray();
            var clock=Stopwatch.StartNew();await Task.WhenAll(targets.Select((m,i)=>m.Device.Player.Start(m.Channel,specs[i],20,clock)));mixerLabel.Text=mixerShape.SelectedItem+" · "+targets.Length+" motors · shared timing";
        }
        void UpdateMixerPreview()
        {
            if(patternLine==null)return;try { var p=MixerPattern(0);double width=((Canvas)patternLine.Parent).ActualWidth;var points=new PointCollection();for(int i=0;i<=100;i++)points.Add(new Point(width*i/100,52-p.At(p.periodMs*i/100.0)*2.3));patternLine.Points=points; }catch(ArgumentException) { patternLine.Points=new PointCollection(); }
        }
        Task SaveMixer()
        {
            MixerPattern(0);var save=new MixerPreset { shape=(string)mixerShape.SelectedItem,low=Number(mixerLow),high=(int)mixerHigh.Value,periodMs=Number(mixerPeriod),durationMs=Number(mixerDuration),transitionMs=Number(mixerTransition),stagger=mixerStagger.IsChecked==true,levels=SelectedMixer().ToDictionary(m=>MixerKey(m.Device,m.Channel),m=>(int)m.Level.Value) };
            var dialog=new SaveFileDialog { Filter="Mixer presets|*.mix.json",FileName="My mix.mix.json" };if(dialog.ShowDialog(window)==true)File.WriteAllText(dialog.FileName,json.Serialize(save));return Task.FromResult(0);
        }
        async Task LoadMixer()
        {
            var dialog=new OpenFileDialog { Filter="Mixer presets|*.mix.json" };if(dialog.ShowDialog(window)!=true)return;
            if(new FileInfo(dialog.FileName).Length>65536)throw new ArgumentException("Mixer file is too large.");var p=json.Deserialize<MixerPreset>(File.ReadAllText(dialog.FileName));
            if(p==null||p.version!=1||!mixerShape.Items.Contains(p.shape)||p.levels==null||p.levels.Values.Any(v=>v<0||v>20))throw new ArgumentException("Invalid mixer preset.");
            new PatternSpec { low=p.low,high=p.high,periodMs=p.periodMs,durationMs=p.durationMs,transitionMs=p.transitionMs }.Validate();
            if(audioCapture!=null)await StopAudioMotors();
            mixerLoading=true;
            mixerShape.SelectedItem=p.shape;mixerLow.Text=p.low.ToString();mixerHigh.Value=p.high;mixerPeriod.Text=p.periodMs.ToString();mixerDuration.Text=p.durationMs.ToString();mixerTransition.Text=p.transitionMs.ToString();mixerStagger.IsChecked=p.stagger;
            foreach(var pair in mixerRows) { pair.Value.Include.IsChecked=p.levels.ContainsKey(pair.Key);if(p.levels.ContainsKey(pair.Key))pair.Value.Level.Value=p.levels[pair.Key]; }
            mixerLoading=false;mixerLabel.Text="Mix loaded · press Play together or Start selected";
        }
        async Task StartAudio()
        {
            var targets=SelectedMixer();if(targets.Length==0)throw new InvalidOperationException("Connect and select at least one motor.");
            int generation=audioGeneration,operation=operatorGeneration;
            await Task.WhenAll(devices.Values.Where(d=>d.Player!=null).Select(d=>d.Player.StopAll()));
            if(generation!=audioGeneration||operation!=operatorGeneration||closing||integration!=null)return;
            var capture=new AudioSync();try { capture.Start(); }catch { capture.Dispose();throw; }
            audioTargets=targets;audioCapture=capture;audioGeneration++;audioLabel.Text="Listening to Windows output · selected motors follow the sound";
        }
        void StopAudio() { audioGeneration++;var capture=audioCapture;audioCapture=null;if(capture!=null) { try { capture.Dispose(); }catch(Exception ex) { Log("Audio: "+ex.Message); } }if(audioLabel!=null)audioLabel.Text="Sound sync stopped"; }
        async Task StopAudioMotors() { var targets=audioTargets??new MixerMotor[0];StopAudio();await Task.WhenAll(targets.Where(m=>m.Device.Player!=null).Select(m=>m.Device.Player.Stop(m.Channel))); }
        async Task AudioTick()
        {
            if(audioCapture==null||audioTickBusy)return;audioTickBusy=true;int generation=audioGeneration;
            Exception failure=null;try {
                if(audioCapture.Error!=null)throw new InvalidOperationException(audioCapture.Error);
                double energy=audioCapture.Read(audioMode.SelectedIndex==0);int level=AudioMeter.Level(energy,audioGain.Value);
                audioLabel.Text="Sound sync · level "+level+" / 20";
                foreach(var m in audioTargets) {
                    if(generation!=audioGeneration||audioCapture==null)return;
                    if(!m.Device.Connected||m.Device.Player==null)throw new InvalidOperationException("A sound-sync motor disconnected.");
                    var player=m.Device.Player;int scaled=m.Include.IsChecked==true?(int)Math.Round(level*m.Level.Value/20.0):0;
                    if(scaled==0) { if(player.Active(m.Channel)||player.Level(m.Channel)!=0)await player.Stop(m.Channel); }
                    else if(!player.UpdateSteady(m.Channel,scaled,500,150))await player.Start(m.Channel,new PatternSpec { mode="Steady",high=scaled,durationMs=500,transitionMs=150 },20);
                }
            }catch(Exception ex) { failure=ex; }finally { audioTickBusy=false; }
            if(failure!=null) { StopAudio();Report(failure);await StopAudioMotors(); }
        }
    }
}

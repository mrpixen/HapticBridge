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
        public Device Device; public int Channel; public CheckBox Include; public Slider Level; public TextBlock Status; public ProgressBar Meter; public Border Card; public LiveMotorTrace Trace;
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
        TabItem mixerTab; StackPanel mixerBody; Panel mixerMotors;
        readonly Dictionary<string,MixerMotor> mixerRows=new Dictionary<string,MixerMotor>();
        ComboBox audioMode; Slider mixerHigh,audioGain; TextBox mixerDuration,mixerTransition;
        TextBlock mixerLabel,audioLabel;
        AudioSync audioCapture; DispatcherTimer mixerTimer; bool audioTickBusy;
        MixerMotor[] audioTargets; int audioGeneration;
        static string MixerKey(Device d,int c) { return d.Address.ToString("X12")+"/"+c; }
        void InitializeMixer()
        {
            InitializeStudioControls();
            InitializeStudioAudio();
            mixerTimer=new DispatcherTimer { Interval=TimeSpan.FromMilliseconds(100) };
            mixerTimer.Tick+=async(s,e)=> { RefreshMixerMeters();await AudioTick();RefreshStudioDock(); };
            mixerTimer.Start();
        }
        MixerMotor[] SelectedMixer() { return mixerRows.Values.Where(m=>m.Include.IsChecked==true&&m.Device.Connected&&m.Device.Player!=null).ToArray(); }
        void RefreshMixer() { RefreshStudioMotors(); }
        void RefreshMixerMeters()
        {
            foreach(var m in mixerRows.Values) {
                int value=m.Device.Player==null?-1:m.Device.Player.Level(m.Channel);
                m.Meter.Value=Math.Max(0,value);
                m.Status.Text=!m.Device.Connected?"Disconnected":value<0?"Sent: unknown":"Sent: "+value+" / 20";
                m.Trace?.Push(value);
            }
            RefreshStudioPlaybackState();
        }
        async Task StartAudio()
        {
            var targets=SelectedMixer();if(targets.Length==0)throw new InvalidOperationException("Connect and select at least one motor.");
            int generation=audioGeneration,operation=operatorGeneration;
            await Task.WhenAll(devices.Values.Where(d=>d.Player!=null).Select(d=>d.Player.StopAll()));
            if(generation!=audioGeneration||operation!=operatorGeneration||closing||integration!=null)return;
            var capture=new AudioSync();try { capture.Start(); }catch { capture.Dispose();throw; }
            audioTargets=mixerRows.Values.Where(m=>m.Device.Connected).ToArray();audioCapture=capture;audioGeneration++;audioLabel.Text="Listening to Windows output · selected motors follow the sound";
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


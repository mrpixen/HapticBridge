using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Markup;
using LovenseConnector;

// Uses synthetic endpoints, capture and actuator transport. No Windows audio is captured.
internal static class AudioRoutingTests
{
    const BindingFlags Members=BindingFlags.Instance|BindingFlags.Public|BindingFlags.NonPublic;
    static int checks;
    static T Get<T>(object app,string name)=>(T)app.GetType().GetField(name,Members).GetValue(app);
    static void Set(object app,string name,object value)=>app.GetType().GetField(name,Members).SetValue(app,value);
    static object Call(object app,string method,params object[] args)=>app.GetType().GetMethod(method,Members).Invoke(app,args);
    static Task Invoke(object app,string method)=>(Task)Call(app,method);
    static void Check(bool value,string label){checks++;if(!value)throw new Exception("Audio routing: "+label);}
    internal static async Task Run()
    {
        var app=(ConnectorApp)RuntimeHelpers.GetUninitializedObject(typeof(ConnectorApp));
        foreach(var field in typeof(ConnectorApp).GetFields(Members))
            if(field.FieldType.IsGenericType&&field.FieldType.GetGenericTypeDefinition()==typeof(Dictionary<,>))field.SetValue(app,Activator.CreateInstance(field.FieldType));
        Window window;using(var stream=AssetFiles.Open("MainWindow.xaml"))window=(Window)XamlReader.Load(stream);
        Set(app,"window",window);Set(app,"tabs",(TabControl)window.FindName("DeviceTabs"));Set(app,"hint",(TextBlock)window.FindName("Hint"));
        Set(app,"summary",(TextBlock)window.FindName("Summary"));Set(app,"activity",(TextBox)window.FindName("ActivityLog"));
        Set(app,"sweepButton",(Button)window.FindName("SweepButton"));Set(app,"feelWriteGate",new SemaphoreSlim(1,1));
        var link=new Link();var player=new Playback(link,new[]{0});
        var device=new Device { Address=1,Name="Synthetic audio actuator",Channels=new[]{0},Connected=true,Player=player };
        device.Motors[0]=new MotorView {Channel=0,Output=new TextBlock()};
        Get<Dictionary<ulong,Device>>(app,"devices").Add(1,device);
        Call(app,"InitializeStudioControls");Call(app,"InitializeStudioAudio");Call(app,"RefreshStudioMotors");
        var outputs=new[]{new AudioOutput { Id="synthetic-a",Name="Review headset" },new AudioOutput { Id="synthetic-b",Name="Review speakers" }};
        Set(app,"audioOutputProvider",new Func<AudioOutput[]>(()=>outputs));
        var captures=new List<Source>();string requested=null;
        Set(app,"audioSourceFactory",new Func<string,IAudioSource>(id=>{requested=id;var capture=new Source();captures.Add(capture);return capture;}));
        var picker=Get<ComboBox>(app,"audioOutputChoice");var label=Get<TextBlock>(app,"audioLabel");
        try {
            await Invoke(app,"RefreshAudioOutputs");
            Check(picker.Items.Count==3&&((AudioOutput)picker.SelectedItem).Id=="","default plus active endpoints are offered");
            Check(captures.Count==0,"enumeration never starts capture");
            picker.SelectedValue="synthetic-b";
            await Invoke(app,"RefreshAudioOutputs");
            Check(((AudioOutput)picker.SelectedItem).Id=="synthetic-b","refresh retains selected endpoint");
            await Invoke(app,"StartAudio");
            Check(requested=="synthetic-b"&&captures[0].Started,"Start resolves selected endpoint");
            Check(label.Text.Contains("Actual synthetic output"),"active label names captured endpoint");
            await Invoke(app,"AudioTick");
            Check(player.Active(0),"synthetic energy reaches the fake output");
            int generation=Get<int>(app,"audioGeneration");picker.SelectedValue="synthetic-a";
            Check(Get<IAudioSource>(app,"audioCapture")==null&&Get<int>(app,"audioGeneration")>generation&&captures[0].Disposed,"selection invalidates and disposes capture synchronously");
            await Task.Delay(60);
            Check(!player.Active(0)&&captures.Count==1,"selection stops output without automatic restart");
            outputs=new[]{outputs[1]};await Invoke(app,"RefreshAudioOutputs");
            Check(((AudioOutput)picker.SelectedItem).Id==""&&label.Text.Contains("unavailable"),"removed output visibly falls back to default while stopped");
            Check(captures.Count==1,"refresh does not restart capture");
            await player.Start(0,new PatternSpec { mode="Steady",high=5,durationMs=2000 },20);
            Set(app,"audioTargets",new[]{Get<Dictionary<string,MixerMotor>>(app,"mixerRows")["000000000001/0"]});
            picker.SelectedValue="synthetic-b";
            await Task.Delay(30);
            Check(player.Active(0),"inactive sound selection cannot stop a different source even with stale target references");
            await Invoke(app,"RefreshAudioOutputs");
            Check(player.Active(0),"inactive sound refresh preserves independent manual output");
            picker.SelectedIndex=0;
            await Invoke(app,"StartAudio");
            Check(requested=="","default choice is resolved at each deliberate Start");
            captures[1].Failure="Synthetic endpoint disconnected";await Invoke(app,"AudioTick");
            Check(Get<IAudioSource>(app,"audioCapture")==null&&captures[1].Disposed&&!player.Active(0),"capture failure stops source and output");
            Check(label.Text=="Synthetic endpoint disconnected","capture failure remains visible");
            await Invoke(app,"StartAudio");await Invoke(app,"AudioTick");
            link.FailNextZero=1;
            await ExpectFailedStop(Invoke(app,"StopAudioMotors"));
            Check(Get<IAudioSource>(app,"audioCapture")==null&&player.Level(0)<0&&label.Text.Contains("unconfirmed"),"failed sound stop retains truthful unconfirmed state after capture closes");
            int zeros=link.ZeroCommands;
            picker.SelectedValue="synthetic-b";await Invoke(app,"RefreshAudioOutputs");
            Check(link.ZeroCommands==zeros&&player.Level(0)<0&&label.Text.Contains("unconfirmed"),"inactive selection and refresh do not retry or conceal a failed stop");
            await Invoke(app,"StopAudioOutput");
            Check(link.ZeroCommands==zeros+1&&player.Level(0)==0&&label.Text=="Sound sync stopped","explicit sound Stop retries once and clears status only after acknowledgement");

            await Invoke(app,"StartAudio");await Invoke(app,"AudioTick");link.FailNextZero=1;
            await ExpectFailedStop(Invoke(app,"StopAudioMotors"));
            await player.Start(0,new PatternSpec { mode="Steady",high=7,durationMs=2000 },20);
            zeros=link.ZeroCommands;
            await Invoke(app,"StopAudioOutput");
            Check(player.Active(0)&&link.ZeroCommands==zeros,"old sound Stop cannot revoke a newer manual owner");
            await GuardRetryAtWriteReady();
            var programs=new[]{new AudioProgram(1234,5678,"Example")};
            Set(app,"audioProgramSupport",new Func<bool>(()=>true));
            Set(app,"audioProgramProvider",new Func<AudioProgram[]>(()=>programs));
            AudioProgram requestedProgram=null;var programCapture=new Source();
            Set(app,"audioProgramFactory",new Func<AudioProgram,IAudioSource>(p=>{requestedProgram=p;return programCapture;}));
            Get<ComboBox>(app,"audioSourceChoice").SelectedIndex=1;
            await Invoke(app,"RefreshAudioPrograms");
            var programPicker=Get<ComboBox>(app,"audioProgramChoice");
            Check(programPicker.Items.Count==1&&programPicker.SelectedItem==null,"program enumeration requires an explicit selection");
            programPicker.SelectedIndex=0;await Invoke(app,"StartAudio");
            Check(ReferenceEquals(requestedProgram,programs[0])&&programCapture.Started,"program mode routes selected identity to its own source factory");
            await Invoke(app,"AudioTick");Check(player.Active(0),"program audio reaches synthetic output");
            programs=new[]{new AudioProgram(1234,5679,"Example restarted")};
            await Invoke(app,"RefreshAudioPrograms");
            Check(programCapture.Disposed&&!player.Active(0)&&programPicker.SelectedItem==null,"refresh stops output and does not retain a reused process id");
            Set(app,"audioProgramSupport",new Func<bool>(()=>false));
            Set(app,"audioProgramProvider",new Func<AudioProgram[]>(()=>throw new Exception("Unsupported Windows must not enumerate programs")));
            await Invoke(app,"RefreshAudioPrograms");
            Check(programPicker.Items.Count==0&&label.Text.Contains("newer Windows"),"unsupported Windows explains availability without enumerating processes");
            await player.Start(0,new PatternSpec { mode="Steady",high=7,durationMs=2000 },20);
            zeros=link.ZeroCommands;bool unsupported=false;
            try {await Invoke(app,"StartAudio");}catch(PlatformNotSupportedException){unsupported=true;}
            Check(unsupported&&link.ZeroCommands==zeros&&player.Active(0),"unsupported program capture is rejected before disrupting independent output");
            Get<ComboBox>(app,"audioSourceChoice").SelectedIndex=0;
            Check(Get<IAudioSource>(app,"audioCapture")==null,"switching source mode never automatically starts capture");
        }finally{await Invoke(app,"StopAudioMotors");await player.StopAll();Get<SemaphoreSlim>(app,"feelWriteGate").Dispose();window.Close();}
        Console.WriteLine("Audio routing: "+checks+" assertions passed (synthetic endpoints; no audio capture or hardware).");
    }
    static async Task ExpectFailedStop(Task stop)
    {
        try { await stop;throw new Exception("Expected synthetic stop failure."); }
        catch(InvalidOperationException ex)when(ex.Message=="Synthetic stop rejected") { }
    }
    static async Task GuardRetryAtWriteReady()
    {
        var link=new Link();var player=new Playback(link,new[]{0});
        try {
            link.FailNextZero=1;
            var failed=player.StopTracked(0,out var claim);await ExpectFailedStop(failed);
            link.HoldNext();
            var retry=player.RetryStop(0,claim,out _);
            await link.Held.Task.WaitAsync(TimeSpan.FromSeconds(2));
            int zeros=link.ZeroCommands;
            var replacement=player.Start(0,new PatternSpec { mode="Steady",high=9,durationMs=2000 },20);
            link.Release.TrySetResult(true);await Task.WhenAll(retry,replacement);
            Check(player.Active(0)&&link.ZeroCommands==zeros,"retry rechecks revision when the write becomes ready and skips a newer manual owner");
            await player.StopAll();link.FailNextZero=1;
            failed=player.StopTracked(0,out claim);await ExpectFailedStop(failed);
            link.HoldNext();retry=player.RetryStop(0,claim,out _);
            await link.Held.Task.WaitAsync(TimeSpan.FromSeconds(2));zeros=link.ZeroCommands;
            player.CancelAll();
            link.Release.TrySetResult(true);await retry;
            Check(!player.Running&&link.ZeroCommands==zeros,"retry also checks whole-player epoch when channel revision is unchanged");
        }finally{link.Release?.TrySetResult(true);await player.StopAll();}
    }
    sealed class Source:IAudioSource
    {
        internal bool Started,Disposed;internal string Failure;
        public string Error=>Failure;public string OutputName=>"Actual synthetic output";
        public void Start(){Started=true;}
        public double Read(bool bassOnly)=>0.16;
        public void Dispose(){Disposed=true;}
    }
    sealed class Link:ICommandLink,ILatestCommandLink
    {
        internal int FailNextZero,ZeroCommands;int holdNext;
        internal TaskCompletionSource<bool> Held,Release;
        internal void HoldNext(){Held=new(TaskCreationOptions.RunContinuationsAsynchronously);Release=new(TaskCreationOptions.RunContinuationsAsynchronously);Interlocked.Exchange(ref holdNext,1);}
        public Task<string> Command(string value)
        {
            if(value.StartsWith("Vibrate")&&value.EndsWith(":0;")) {
                Interlocked.Increment(ref ZeroCommands);
                if(Interlocked.Exchange(ref FailNextZero,0)!=0)throw new InvalidOperationException("Synthetic stop rejected");
            }
            return Task.FromResult("OK");
        }
        public Task<string> Command(string value,CancellationToken token){token.ThrowIfCancellationRequested();return Command(value);}
        public async Task<string> CommandLatest(Func<string> command,CancellationToken token)
        {
            if(Interlocked.Exchange(ref holdNext,0)!=0) { Held.TrySetResult(true);await Release.Task.WaitAsync(token); }
            token.ThrowIfCancellationRequested();var value=command();return value==null?"":await Command(value,token);
        }
    }
}

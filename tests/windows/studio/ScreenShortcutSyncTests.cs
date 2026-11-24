using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Controls;
using LovenseConnector;

internal static partial class ScreenSyncTests
{
    sealed class ScreenKeys : IStopHotkeyRegistrar
    {
        readonly Dictionary<int,(uint Modifiers,uint Key)> keys=new();
        public int Count=>keys.Count;
        public bool Register(int id,uint modifiers,uint key,out int error)
        {error=keys.Values.Contains((modifiers,key))?1409:0;if(error!=0)return false;keys.Add(id,(modifiers,key));return true;}
        public bool Unregister(int id,out int error){error=0;return keys.Remove(id);}
    }
    static async Task WaitScreenShortcut(ConnectorApp app)
    {
        var end=DateTime.UtcNow.AddSeconds(3);
        while(Get<bool>(app,"screenShortcutStarting")) {if(DateTime.UtcNow>=end)throw new Exception("Synthetic shortcut timeout");await Task.Delay(10);}
    }
    static async Task<Capture> ScreenShortcutGameplay(ConnectorApp app,Capture capture,Link[] links,Playback[] players)
    {
        Call(app,"InitializeStudioSettings");var keys=new ScreenKeys();Call(app,"AttachStopShortcut",keys);
        Call(app,"ApplyStopShortcut",true);Call(app,"ApplyScreenShortcut",true);
        bool Press(string name) {
            var binding=Get<GlobalStopHotkey>(app,name);var chord=binding.ActiveChord.Value;
            return (bool)Call(app,"HandleAppShortcut",GlobalStopHotkey.HotkeyMessage,binding.ActiveId.Value,((long)chord.Key<<16)|chord.Modifiers);
        }
        int Positive()=>links.Sum(l=>l.Positive.Count);
        try {
            Check(keys.Count==2&&!Get<bool>(app,"screenArmed"),"installing two fake bindings leaves screen feedback off");
            await Feed(app,capture,"100",3,"25");int commands=links.Sum(l=>l.Commands),positive=Positive();
            Set(app,"screenCapture",null);await Invoke(app,"EnableScreenFromShortcut");
            Check(Get<object>(app,"screenCapture")==null&&!Get<bool>(app,"screenArmed")&&links.Sum(l=>l.Commands)==commands,"shortcut cannot start capture or send commands without a preview");
            Set(app,"screenCapture",capture);capture.Foreground=false;await Invoke(app,"EnableScreenFromShortcut");
            Check(!Get<bool>(app,"screenArmed")&&links.Sum(l=>l.Commands)==commands,"shortcut in another foreground window does not take over output");
            capture.Foreground=true;Set(app,"screenFrozen",true);await Invoke(app,"EnableScreenFromShortcut");
            Check(!Get<bool>(app,"screenArmed")&&links.Sum(l=>l.Commands)==commands,"frozen preview cannot be enabled by the shortcut");Set(app,"screenFrozen",false);
            await Feed(app,capture,"",1,"25");await Invoke(app,"EnableScreenFromShortcut");
            Check(!Get<bool>(app,"screenArmed")&&links.Sum(l=>l.Commands)==commands,"unreadable health rejects shortcut enable before device handoff");
            await Feed(app,capture,"0",1,"25");await Invoke(app,"EnableScreenFromShortcut");
            Check(!Get<bool>(app,"screenArmed")&&links.Sum(l=>l.Commands)==commands,"zero health cannot be re-enabled by the shortcut");
            await Feed(app,capture,"100",1,"25");var frame=Get<ScreenPixels>(app,"screenFrame");Set(app,"screenFrame",new ScreenPixels(frame.Bgra,frame.Width,frame.Height,ScreenCapture.Now-2));
            await Invoke(app,"EnableScreenFromShortcut");Check(!Get<bool>(app,"screenArmed")&&links.Sum(l=>l.Commands)==commands,"stale preview cannot enable feedback");
            await Feed(app,capture,"100",3,"25");Check(Press("screenShortcut"),"matching screen shortcut is dispatched to its action");await WaitScreenShortcut(app);
            Check(Get<bool>(app,"screenArmed")&&Positive()==positive,"explicit shortcut arms a fresh live HUD without sending a test pulse");
            int operation=Get<int>(app,"operatorGeneration");commands=links.Sum(l=>l.Commands);Press("screenShortcut");await WaitScreenShortcut(app);
            Check(Get<int>(app,"operatorGeneration")==operation&&links.Sum(l=>l.Commands)==commands&&Get<bool>(app,"screenArmed"),"pressing enable while already armed does not stop or restart playback");
            await Feed(app,capture,"100",3,"25");await Feed(app,capture,"80",3,"25");await Task.Delay(350);
            Check(Positive()>positive&&players.All(p=>p.Level(0)==0),"feedback enabled by the shortcut follows a later damage event and stops its pulse");
            await Feed(app,capture,"0",3,"25");await Task.Delay(450);await Invoke(app,"ScreenTick");
            Check(!Get<bool>(app,"screenArmed")&&Get<object>(app,"screenCapture")!=null,"death ends feedback while retaining the preview for an explicit later enable");
            await Feed(app,capture,"100",3,"25");Press("screenShortcut");await WaitScreenShortcut(app);
            Check(Get<bool>(app,"screenArmed"),"a fresh positive HUD can be explicitly re-enabled after death without switching windows");await Invoke(app,"DisarmScreen");
            foreach(string cancel in new[]{"focus","binding","Stop all"}) {
                Set(app,"screenCapture",capture);capture.Foreground=true;await Feed(app,capture,"100",3,"25");
                Call(app,"ApplyScreenShortcut",true);positive=Positive();links[0].Block=new TaskCompletionSource<bool>();links[0].Entered=new TaskCompletionSource<bool>();
                var pending=Invoke(app,"EnableScreenFromShortcut");
                try {
                    await links[0].Entered.Task.WaitAsync(TimeSpan.FromSeconds(2));operation=Get<int>(app,"operatorGeneration");
                    await Invoke(app,"EnableScreenFromShortcut");Check(operation==Get<int>(app,"operatorGeneration"),"repeated enable during a pending handoff never queues another start");
                    if(cancel=="focus")capture.Foreground=false;
                    else if(cancel=="binding")Call(app,"ApplyScreenShortcut",false);
                    else Check(Press("stopShortcut"),"Stop all remains independently dispatchable during a pending screen enable");
                }finally{links[0].Block.TrySetResult(true);await pending;links[0].Block=null;}
                Check(!Get<bool>(app,"screenArmed")&&Positive()==positive,cancel+" cancels pending shortcut enable without delayed output");
                await Invoke(app,"StopScreenCapture");capture=new Capture();Set(app,"screenCapture",capture);
            }
            await ScreenArmReconnect(app,capture,links);
        }finally {
            links[0].Block?.TrySetResult(true);links[0].Block=null;await Invoke(app,"DisarmScreen");Call(app,"CloseStopShortcut");
        }
        Check(keys.Count==0,"shortcut fixture releases both fake registrations without using Windows hooks");
        return capture;
    }
    static async Task ScreenArmReconnect(ConnectorApp app,Capture capture,Link[] links)
    {
        var device=Get<Dictionary<ulong,Device>>(app,"devices")[1];var original=device.Player;
        foreach(string phase in new[]{"source handoff","motor stop"})
        foreach(string action in new[]{"ArmScreen","EnableScreenFromShortcut"}) {
            var replacementLink=new Link();var replacement=new Playback(replacementLink,new[]{0});
            Set(app,"screenCapture",capture);capture.Foreground=true;Call(app,"ApplyScreenShortcut",true);
            await Feed(app,capture,"100",3,"25");
            if(phase=="source handoff") {
                Set(app,"audioLabel",new TextBlock());Set(app,"audioCapture",new AudioSource());
                Set(app,"audioTargets",new[]{new MixerMotor {Device=Get<Dictionary<ulong,Device>>(app,"devices")[2],Channel=0}});
            }
            links[1].Block=new TaskCompletionSource<bool>();links[1].Entered=new TaskCompletionSource<bool>();
            var pending=Invoke(app,action);
            try {
                await links[1].Entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
                // First target completed its stop; it reconnects while another target is slow.
                device.Generation++;device.Player=replacement;Set(app,"screenFrame",ShieldPixels("100","25"));
                links[1].Block.TrySetResult(true);await pending;links[1].Block=null;
                Check(!Get<bool>(app,"screenArmed")&&replacementLink.Commands==0,action+" cannot adopt a replacement connection during "+phase);
                await Feed(app,capture,"100",3,"25");await Invoke(app,action);
                Check(Get<bool>(app,"screenArmed")&&replacementLink.Positive.IsEmpty,action+" can arm the replacement only after a fresh explicit request following "+phase);
            }finally {
                links[1].Block?.TrySetResult(true);await pending;links[1].Block=null;
                await Invoke(app,"DisarmScreen");await replacement.StopAll();device.Generation++;device.Player=original;
            }
        }
    }
}

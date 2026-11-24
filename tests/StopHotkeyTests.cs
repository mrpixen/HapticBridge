using System;
using System.Collections.Generic;
using System.Linq;
using LovenseConnector;

internal static class StopHotkeyTests
{
    static int checks;
    static void Check(bool value,string message){checks++;if(!value)throw new Exception("Stop shortcut: "+message);}
    sealed class Registrar : IStopHotkeyRegistrar
    {
        public readonly Dictionary<int,(uint Modifiers,uint Key)> Bindings=new();
        public readonly HashSet<int> FailRelease=new();
        public Action<int> Registering,Releasing;
        public bool Conflict;
        public int Calls;
        public bool Register(int id,uint modifiers,uint key,out int error)
        { Calls++;Registering?.Invoke(id);error=Conflict?1409:0;if(Conflict)return false;Bindings.Add(id,(modifiers,key));return true; }
        public bool Unregister(int id,out int error)
        { Releasing?.Invoke(id);error=FailRelease.Contains(id)?5:0;return error==0&&Bindings.Remove(id); }
    }
    static long Packed(StopHotkeyChord chord)=>((long)chord.Key<<16)|chord.Modifiers;
    static void Main()
    {
        var registrar=new Registrar();int stopped=0;using var hotkey=new GlobalStopHotkey(registrar,()=>stopped++);
        var first=StopHotkeyChord.Default;var second=new StopHotkeyChord(3,0x78);
        Check(!hotkey.Active&&registrar.Calls==0,"construction does not register a shortcut");
        Check(hotkey.Configure(false,first)&&registrar.Calls==0,"disabled configuration makes no registration");
        foreach(var bad in new[]{new StopHotkeyChord(0,0x77),new(2,0x77),new(8,0x77),new(6,0x7b),new(3,0x1b)})
            Check(!hotkey.Configure(true,bad)&&registrar.Calls==0,"reserved or unlisted chord is rejected before the OS boundary");
        Check(hotkey.Configure(true,first)&&hotkey.Active&&hotkey.Error=="","explicit enable registers successfully");
        int oldId=hotkey.ActiveId.Value;
        Check(registrar.Bindings[oldId].Modifiers==(first.Modifiers|GlobalStopHotkey.NoRepeat),"registered chord suppresses keyboard auto-repeat");
        Check(!hotkey.HandleMessage(0,oldId,Packed(first))&&!hotkey.HandleMessage(GlobalStopHotkey.HotkeyMessage,oldId+1,Packed(first))&&
            !hotkey.HandleMessage(GlobalStopHotkey.HotkeyMessage,oldId,Packed(second))&&stopped==0,"only the owned message ID and chord can invoke Stop");
        Check(hotkey.HandleMessage(GlobalStopHotkey.HotkeyMessage,oldId,Packed(first))&&stopped==1,"matching press invokes Stop synchronously");
        Check(hotkey.HandleMessage(GlobalStopHotkey.HotkeyMessage,oldId,Packed(first))&&stopped==2,"a second distinct press is never coalesced away");
        Check(hotkey.Configure(true,first)&&registrar.Calls==1,"reapplying active chord does not duplicate registration");
        registrar.Conflict=true;
        Check(!hotkey.Configure(true,second)&&hotkey.ActiveId==oldId&&hotkey.ActiveChord==first&&registrar.Bindings.Count==1&&hotkey.Error.Contains("already in use"),"replacement conflict preserves the working chord and reports failure");
        registrar.Conflict=false;bool premature=false;
        registrar.Registering=id=>premature|=hotkey.HandleMessage(GlobalStopHotkey.HotkeyMessage,id,Packed(second));
        Check(hotkey.Configure(true,second)&&!premature,"candidate callbacks are ignored until replacement commits");
        registrar.Registering=null;int newId=hotkey.ActiveId.Value;
        Check(newId!=oldId&&registrar.Bindings.Count==1&&!registrar.Bindings.ContainsKey(oldId),"successful replacement releases the previous OS registration");
        Check(!hotkey.HandleMessage(GlobalStopHotkey.HotkeyMessage,oldId,Packed(first)),"late messages for the replaced chord are ignored");
        bool duringDisable=false;registrar.Releasing=id=>duringDisable|=hotkey.HandleMessage(GlobalStopHotkey.HotkeyMessage,id,Packed(second));
        Check(hotkey.Configure(false,second)&&!duringDisable&&!hotkey.Active&&!hotkey.HasRegistrations,"disable invalidates callbacks before releasing registration");
        registrar.Releasing=null;
        Check(!hotkey.HandleMessage(GlobalStopHotkey.HotkeyMessage,newId,Packed(second)),"queued disabled messages cannot trigger Stop");
        Check(hotkey.Configure(true,second)&&hotkey.ActiveId!=newId&&!hotkey.HandleMessage(GlobalStopHotkey.HotkeyMessage,newId,Packed(second)),"reenabling the same chord never revives an old registration ID");
        int retained=hotkey.ActiveId.Value;registrar.FailRelease.Add(retained);
        Check(!hotkey.Configure(true,first)&&hotkey.ActiveId==retained&&registrar.Bindings.Count==1&&hotkey.Error.Contains("previous shortcut remains active"),"failed replacement cleanup rolls back the new chord");
        Check(!hotkey.Configure(false,second)&&!hotkey.Active&&hotkey.HasRegistrations&&hotkey.Error.Contains("Retry Disable"),"failed OS release is reported without leaving callbacks enabled");
        Check(!hotkey.HandleMessage(GlobalStopHotkey.HotkeyMessage,retained,Packed(second)),"failed unregister still disables app handling");
        registrar.FailRelease.Clear();Check(hotkey.Configure(false,second)&&!hotkey.HasRegistrations,"Disable can retry an unconfirmed release");
        hotkey.Configure(true,first);int closing=hotkey.ActiveId.Value;hotkey.Dispose();
        Check(registrar.Bindings.Count==0&&!hotkey.HandleMessage(GlobalStopHotkey.HotkeyMessage,closing,Packed(first)),"shutdown releases registrations and ignores late callbacks");
        Check(!hotkey.Configure(true,first),"a disposed controller cannot register again");
        SeparateActionRanges();
        Console.WriteLine("Stop shortcut: "+checks+" assertions passed (fake registrar only).");
    }
    static void SeparateActionRanges()
    {
        var registrar=new Registrar();int stops=0,enables=0;
        using var stop=new GlobalStopHotkey(registrar,()=>stops++,0x4100,0x4101);
        using var screen=new GlobalStopHotkey(registrar,()=>enables++,0x8000,0x8001);
        Check(stop.Configure(true,StopHotkeyChord.Default)&&screen.Configure(true,StopHotkeyChord.ScreenDefault)&&registrar.Bindings.Count==2,"two actions register independently on the same window boundary");
        int stopId=stop.ActiveId.Value,screenId=screen.ActiveId.Value;
        Check(stopId!=screenId&&!stop.HandleMessage(GlobalStopHotkey.HotkeyMessage,screenId,Packed(StopHotkeyChord.ScreenDefault))&&screen.HandleMessage(GlobalStopHotkey.HotkeyMessage,screenId,Packed(StopHotkeyChord.ScreenDefault))&&stops==0&&enables==1,"screen shortcut cannot invoke Stop all");
        Check(!screen.HandleMessage(GlobalStopHotkey.HotkeyMessage,stopId,Packed(StopHotkeyChord.Default))&&stop.HandleMessage(GlobalStopHotkey.HotkeyMessage,stopId,Packed(StopHotkeyChord.Default))&&stops==1&&enables==1,"Stop all cannot invoke screen enable");
        Check(screen.Configure(false,StopHotkeyChord.ScreenDefault)&&stop.Active&&registrar.Bindings.Count==1,"disabling one action preserves the other's binding");
        Check(screen.Configure(true,StopHotkeyChord.ScreenDefault)&&screen.ActiveId!=screenId&&!screen.HandleMessage(GlobalStopHotkey.HotkeyMessage,screenId,Packed(StopHotkeyChord.ScreenDefault)),"reenabling ignores queued messages for an older screen binding");
        int retained=screen.ActiveId.Value;
        Check(!screen.Configure(true,new StopHotkeyChord(3,0x79))&&screen.ActiveId==retained&&stop.ActiveId==stopId&&registrar.Bindings.Count==2,"exhausting an action's ID range preserves both current bindings");
        foreach(var range in new[]{(-1,1),(1,0xc000),(2,1)}) {
            int before=registrar.Calls;bool rejected=false;
            try{using var invalid=new GlobalStopHotkey(registrar,()=>{},range.Item1,range.Item2);}catch(ArgumentOutOfRangeException){rejected=true;}
            Check(rejected&&registrar.Calls==before,"invalid application ID range is rejected without registration");
        }
    }
}

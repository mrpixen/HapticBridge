using System;
using System.Collections.Generic;
using System.Linq;

namespace LovenseConnector
{
    internal readonly record struct StopHotkeyChord(uint Modifiers,uint Key)
    {
        public static readonly uint[] ModifierChoices={3,6,5,7};
        public static readonly string[] ModifierLabels={"Ctrl + Alt","Ctrl + Shift","Alt + Shift","Ctrl + Alt + Shift"};
        public static StopHotkeyChord Default=>new(6,0x77); // Ctrl + Shift + F8; never enabled by default.
        public static StopHotkeyChord ScreenDefault=>new(6,0x78); // Ctrl + Shift + F9.
        public bool Valid=>ModifierChoices.Contains(Modifiers)&&Key>=0x75&&Key<=0x7a;
        public string Label=>Valid?ModifierLabels[Array.IndexOf(ModifierChoices,Modifiers)]+" + F"+(Key-0x6f):"Choose a shortcut";
    }

    internal interface IStopHotkeyRegistrar
    {
        bool Register(int id,uint modifiers,uint key,out int error);
        bool Unregister(int id,out int error);
    }

    // UI-thread controller. The OS boundary is replaceable for offline tests.
    internal sealed class GlobalStopHotkey : IDisposable
    {
        public const int HotkeyMessage=0x0312;
        public const uint NoRepeat=0x4000;
        readonly IStopHotkeyRegistrar registrar;
        readonly Action stop;
        readonly Dictionary<int,StopHotkeyChord> registrations=new();
        int nextId;
        readonly int lastId;
        int? activeId;
        bool disposed;
        public bool Active=>activeId.HasValue;
        public bool HasRegistrations=>registrations.Count>0;
        public int? ActiveId=>activeId;
        public StopHotkeyChord? ActiveChord=>activeId.HasValue?registrations[activeId.Value]:null;
        public string Error {get;private set;}="";

        public GlobalStopHotkey(IStopHotkeyRegistrar registrar,Action stop,int firstId=0x4100,int lastId=0xbfff)
        {
            if(firstId<0||lastId>0xbfff||firstId>lastId)throw new ArgumentOutOfRangeException(nameof(firstId));
            this.registrar=registrar??throw new ArgumentNullException(nameof(registrar));this.stop=stop??throw new ArgumentNullException(nameof(stop));
            nextId=firstId;this.lastId=lastId;
        }

        bool Release(int id)
        {
            if(!registrar.Unregister(id,out _))return false;
            registrations.Remove(id);return true;
        }
        public bool Configure(bool enabled,StopHotkeyChord chord)
        {
            Error="";
            if(disposed){Error="Shortcut is unavailable while closing.";return false;}
            if(!enabled)
            {
                // Invalidate callbacks before asking Windows to release the chord.
                activeId=null;
                foreach(int id in registrations.Keys.ToArray())Release(id);
                if(HasRegistrations)Error="Could not release the shortcut. Retry Disable or close HapticBridge.";
                return !HasRegistrations;
            }
            if(!chord.Valid){Error="Choose two or three modifiers and a key from F6 to F11.";return false;}
            foreach(int id in registrations.Keys.Where(id=>id!=activeId).ToArray())Release(id);
            if(registrations.Keys.Any(id=>id!=activeId)){Error="Could not release an old shortcut. Retry Disable or close HapticBridge.";return false;}
            if(ActiveChord==chord)return true;
            // Never reuse an ID: a queued message for a replaced binding must not
            // become current again, even when the user later chooses that chord.
            if(nextId>lastId){Error="Restart HapticBridge before changing the shortcut again.";return false;}
            int candidate=nextId++;
            if(!registrar.Register(candidate,chord.Modifiers|NoRepeat,chord.Key,out int error))
            {
                Error=error==1409?"That shortcut is already in use. Choose another.":"Windows could not register the shortcut (error "+error+").";
                return false;
            }
            registrations[candidate]=chord;
            if(activeId.HasValue&&!Release(activeId.Value))
            {
                Release(candidate);
                Error="Could not replace the shortcut. The previous shortcut remains active.";
                if(registrations.Count>1)Error+=" Retry Disable or close HapticBridge to release the pending shortcut.";
                return false;
            }
            activeId=candidate;return true;
        }
        public bool HandleMessage(int message,int id,long data)
        {
            if(disposed||message!=HotkeyMessage||id!=activeId)return false;
            var chord=ActiveChord.Value;
            if((uint)(data&0xffff)!=chord.Modifiers||(uint)((data>>16)&0xffff)!=chord.Key)return false;
            // Invoke synchronously; every separate press must cancel pending
            // starts even when an earlier stop is still awaiting its transport.
            stop();return true;
        }
        public void Dispose()
        {
            if(disposed)return;
            activeId=null;disposed=true;
            foreach(int id in registrations.Keys.ToArray())Release(id);
        }
    }
}

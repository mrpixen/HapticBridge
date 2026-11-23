using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows.Automation;
using System.Windows.Controls;
using LovenseConnector;

internal static partial class StudioIntegrationTests
{
    sealed class ShortcutRegistrar : IStopHotkeyRegistrar
    {
        public readonly HashSet<int> Active=new();
        public int Registered;
        public bool Conflict;
        public bool Register(int id,uint modifiers,uint key,out int error){Registered++;error=Conflict?1409:0;if(Conflict)return false;Active.Add(id);return true;}
        public bool Unregister(int id,out int error){error=0;return Active.Remove(id);}
    }
    static void SetupShortcutSettings(Fixture fixture,string folder,ShortcutRegistrar registrar)
    {
        Set(fixture.App,"dataDir",folder);Set(fixture.App,"json",new System.Web.Script.Serialization.JavaScriptSerializer());
        Set(fixture.App,"studioUiPreferences",new StudioUiPreferences());Call(fixture.App,"InitializeStudioSettings");Call(fixture.App,"AttachStopShortcut",registrar);
    }
    static async Task ShortcutPreferencesAndConflicts()
    {
        string folder=Path.Combine(Path.GetTempPath(),"HapticBridge-shortcut-test-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(folder);
        try
        {
            await using(var fixture=new Fixture())
            {
                var registrar=new ShortcutRegistrar();SetupShortcutSettings(fixture,folder,registrar);
                try
                {
                    Check(registrar.Registered==0&&!Get<StudioUiPreferences>(fixture.App,"studioUiPreferences").StopEnabled,"Settings defaults never register a global shortcut");
                    var modifiers=Get<ComboBox>(fixture.App,"stopShortcutModifiers");var key=Get<ComboBox>(fixture.App,"stopShortcutKey");
                    Check(AutomationProperties.GetName(modifiers).Length>0&&AutomationProperties.GetName(key).Length>0,"shortcut choices have persistent accessible names");
                    modifiers.SelectedIndex=0;key.SelectedIndex=3;Call(fixture.App,"ApplyStopShortcut",true);
                    var preferences=Get<StudioUiPreferences>(fixture.App,"studioUiPreferences");
                    Check(preferences.StopEnabled&&preferences.StopModifiers==3&&preferences.StopKey==0x78&&Get<TextBlock>(fixture.App,"stopShortcutStatus").Text.Contains("Active · Ctrl + Alt + F9"),"successful registration displays and persists the actual active chord");
                    Get<ComboBox>(fixture.App,"studioMaterialChoice").SelectedItem="Solid";
                    var saved=JsonSerializer.Deserialize<StudioUiPreferences>(File.ReadAllText(Path.Combine(folder,"ui-preferences.json")));
                    Check(saved.Material=="Solid"&&saved.StopEnabled&&saved.StopKey==0x78&&registrar.Registered==1,"appearance save preserves shortcut settings without registering again");
                    registrar.Conflict=true;modifiers.SelectedIndex=1;key.SelectedIndex=4;Call(fixture.App,"ApplyStopShortcut",true);
                    saved=JsonSerializer.Deserialize<StudioUiPreferences>(File.ReadAllText(Path.Combine(folder,"ui-preferences.json")));
                    string status=Get<TextBlock>(fixture.App,"stopShortcutStatus").Text;
                    Check(saved.StopEnabled&&saved.StopModifiers==3&&saved.StopKey==0x78&&status.Contains("Ctrl + Alt + F9")&&status.Contains("already in use"),"conflicting replacement retains and reports the old working shortcut");
                    Get<ComboBox>(fixture.App,"studioMotionChoice").SelectedIndex=1;
                    Check(registrar.Registered==2,"appearance changes never retry a failed shortcut registration");
                    Call(fixture.App,"CloseStopShortcut");Check(registrar.Active.Count==0,"app shortcut teardown unregisters its owned shortcut");
                }
                finally{Call(fixture.App,"CloseStopShortcut");}
            }
            string enabledPreferences=File.ReadAllText(Path.Combine(folder,"ui-preferences.json"));
            await using(var fixture=new Fixture())
            {
                var registrar=new ShortcutRegistrar {Conflict=true};SetupShortcutSettings(fixture,folder,registrar);
                try
                {
                    Check(registrar.Registered==1&&!Get<StudioUiPreferences>(fixture.App,"studioUiPreferences").StopEnabled&&
                        Get<TextBlock>(fixture.App,"stopShortcutStatus").Text.Contains("already in use"),"startup conflict reports unavailable instead of claiming the saved shortcut is active");
                    Get<ComboBox>(fixture.App,"studioMaterialChoice").SelectedItem="Glass";
                    Check(registrar.Registered==1&&!JsonSerializer.Deserialize<StudioUiPreferences>(File.ReadAllText(Path.Combine(folder,"ui-preferences.json"))).StopEnabled,
                        "appearance save after startup conflict neither retries registration nor persists a false enabled state");
                }
                finally{Call(fixture.App,"CloseStopShortcut");}
            }
            File.WriteAllText(Path.Combine(folder,"ui-preferences.json"),enabledPreferences);
            await using(var fixture=new Fixture())
            {
                var registrar=new ShortcutRegistrar();SetupShortcutSettings(fixture,folder,registrar);
                try
                {
                    Check(registrar.Registered==1&&Get<StudioUiPreferences>(fixture.App,"studioUiPreferences").Material=="Solid","previous explicit enable is restored independently of appearance");
                    Call(fixture.App,"ApplyStopShortcut",false);
                    Check(registrar.Active.Count==0&&!JsonSerializer.Deserialize<StudioUiPreferences>(File.ReadAllText(Path.Combine(folder,"ui-preferences.json"))).StopEnabled,"Disable releases registration and persists the opt-out");
                }
                finally{Call(fixture.App,"CloseStopShortcut");}
            }
            foreach(string content in new[]{"{\"Material\":\"Solid\",\"StopEnabled\":true,\"StopModifiers\":6,\"StopKey\":123}","broken json"})
            {
                File.WriteAllText(Path.Combine(folder,"ui-preferences.json"),content);
                await using var fixture=new Fixture();var registrar=new ShortcutRegistrar();SetupShortcutSettings(fixture,folder,registrar);
                try{Check(registrar.Registered==0&&!Get<StudioUiPreferences>(fixture.App,"studioUiPreferences").StopEnabled,"invalid or damaged shortcut preferences remain disabled");}
                finally{Call(fixture.App,"CloseStopShortcut");}
            }
        }
        finally{Directory.Delete(folder,true);}
    }
    static async Task ShortcutCancelsEachPendingStart()
    {
        await using var fixture=new Fixture();Set(fixture.App,"studioUiPreferences",new StudioUiPreferences());
        var registrar=new ShortcutRegistrar();Call(fixture.App,"AttachStopShortcut",registrar);var shortcut=Get<GlobalStopHotkey>(fixture.App,"stopShortcut");
        try
        {
            var chord=StopHotkeyChord.Default;shortcut.Configure(true,chord);long message=((long)chord.Key<<16)|chord.Modifiers;
            fixture.Dual.HoldNextPresetStop=true;var firstStart=fixture.Start();await fixture.Dual.PresetEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));
            int first=Get<int>(fixture.App,"operatorGeneration");shortcut.HandleMessage(GlobalStopHotkey.HotkeyMessage,shortcut.ActiveId.Value,message);
            Check(Get<int>(fixture.App,"operatorGeneration")>first&&!Get<bool>(fixture.App,"apiEnabled"),"global Stop cancels ownership synchronously before its slow transport completes");
            var newerStart=fixture.Start();int newer=Get<int>(fixture.App,"operatorGeneration");
            shortcut.HandleMessage(GlobalStopHotkey.HotkeyMessage,shortcut.ActiveId.Value,message);
            Check(Get<int>(fixture.App,"operatorGeneration")>newer,"second separate Stop press cancels a newer pending start while the first Stop awaits");
            fixture.Dual.ReleasePreset.TrySetResult();await Task.WhenAll(firstStart,newerStart).WaitAsync(TimeSpan.FromSeconds(3));
            Check(!fixture.Commands().Any(command=>command.Nonzero)&&!Get<bool>(fixture.App,"studioRunning"),"neither pending start can resume output after global Stop");
            Check(!fixture.Window.IsVisible,"global shortcut tests never display the app or register with Windows");
        }
        finally{fixture.Dual.ReleasePreset.TrySetResult();Call(fixture.App,"CloseStopShortcut");}
    }
}

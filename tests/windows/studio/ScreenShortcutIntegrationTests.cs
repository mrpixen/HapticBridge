using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows.Automation;
using System.Windows.Controls;
using LovenseConnector;

internal static partial class StudioIntegrationTests
{
    static async Task ScreenShortcutPreferencesAndConflicts()
    {
        string folder=Path.Combine(Path.GetTempPath(),"HapticBridge-screen-key-test-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(folder);
        try {
            await using(var fixture=new Fixture()) {
                var registrar=new ShortcutRegistrar();SetupShortcutSettings(fixture,folder,registrar);
                try {
                    Check(registrar.Registered==0&&!Get<StudioUiPreferences>(fixture.App,"studioUiPreferences").ScreenShortcutEnabled,"screen shortcut defaults to off without registering with the OS boundary");
                    var modifiers=Get<ComboBox>(fixture.App,"screenShortcutModifiers");var key=Get<ComboBox>(fixture.App,"screenShortcutKey");
                    Check(AutomationProperties.GetName(modifiers).Length>0&&AutomationProperties.GetName(key).Length>0,"screen shortcut controls have persistent accessible names");
                    Call(fixture.App,"ApplyStopShortcut",true);Call(fixture.App,"ApplyScreenShortcut",true);
                    var stop=Get<GlobalStopHotkey>(fixture.App,"stopShortcut");var screen=Get<GlobalStopHotkey>(fixture.App,"screenShortcut");
                    Check(registrar.Active.Count==2&&stop.ActiveId!=screen.ActiveId&&Get<TextBlock>(fixture.App,"screenShortcutStatus").Text.Contains("Ctrl + Shift + F9"),"both default actions register with independent IDs and reported chords");
                    modifiers.SelectedIndex=1;key.SelectedIndex=2;Call(fixture.App,"ApplyScreenShortcut",true);
                    var saved=JsonSerializer.Deserialize<StudioUiPreferences>(File.ReadAllText(Path.Combine(folder,"ui-preferences.json")));
                    Check(saved.ScreenShortcutEnabled&&saved.ScreenShortcutKey==0x78&&saved.StopEnabled&&registrar.Active.Count==2&&Get<TextBlock>(fixture.App,"screenShortcutStatus").Text.Contains("already in use"),"assigning the Stop all chord to screen enable retains both previous working bindings");
                    Get<ComboBox>(fixture.App,"studioMaterialChoice").SelectedItem="Solid";
                    saved=JsonSerializer.Deserialize<StudioUiPreferences>(File.ReadAllText(Path.Combine(folder,"ui-preferences.json")));
                    Check(saved.Material=="Solid"&&saved.ScreenShortcutEnabled&&saved.ScreenShortcutKey==0x78,"appearance saving preserves the active screen shortcut after a conflict");
                    Check(!Get<bool>(fixture.App,"screenArmed")&&Get<object>(fixture.App,"screenCapture")==null&&!fixture.Commands().Any(c=>c.Nonzero),"enabling a shortcut alone never enables capture or output");
                }finally{Call(fixture.App,"CloseStopShortcut");Check(registrar.Active.Count==0,"window teardown releases both shortcut actions");}
            }
            string enabled=File.ReadAllText(Path.Combine(folder,"ui-preferences.json"));
            await using(var fixture=new Fixture()) {
                var registrar=new ShortcutRegistrar();SetupShortcutSettings(fixture,folder,registrar);
                try {
                    Check(registrar.Active.Count==2&&Get<StudioUiPreferences>(fixture.App,"studioUiPreferences").ScreenShortcutEnabled&&!Get<bool>(fixture.App,"screenArmed"),"saved explicit shortcut choices reload without arming screen output");
                    Call(fixture.App,"ApplyScreenShortcut",false);
                    Check(registrar.Active.Count==1&&Get<GlobalStopHotkey>(fixture.App,"stopShortcut").Active&&!JsonSerializer.Deserialize<StudioUiPreferences>(File.ReadAllText(Path.Combine(folder,"ui-preferences.json"))).ScreenShortcutEnabled,"screen shortcut opt-out persists without disabling Stop all");
                }finally{Call(fixture.App,"CloseStopShortcut");}
            }
            File.WriteAllText(Path.Combine(folder,"ui-preferences.json"),enabled);
            await using(var fixture=new Fixture()) {
                var registrar=new ShortcutRegistrar {Conflict=true};SetupShortcutSettings(fixture,folder,registrar);
                try{Check(!Get<StudioUiPreferences>(fixture.App,"studioUiPreferences").ScreenShortcutEnabled&&Get<TextBlock>(fixture.App,"screenShortcutStatus").Text.Contains("already in use")&&!Get<bool>(fixture.App,"screenArmed"),"a startup conflict never reports the screen shortcut active or enables output");}
                finally{Call(fixture.App,"CloseStopShortcut");}
            }
            File.WriteAllText(Path.Combine(folder,"ui-preferences.json"),"{\"ScreenShortcutEnabled\":true,\"ScreenShortcutModifiers\":2,\"ScreenShortcutKey\":123}");
            await using(var fixture=new Fixture()) {
                var registrar=new ShortcutRegistrar();SetupShortcutSettings(fixture,folder,registrar);
                try{Check(registrar.Registered==0&&!Get<StudioUiPreferences>(fixture.App,"studioUiPreferences").ScreenShortcutEnabled,"invalid screen shortcut preferences remain off");}
                finally{Call(fixture.App,"CloseStopShortcut");}
            }
        }finally{Directory.Delete(folder,true);}
    }
}

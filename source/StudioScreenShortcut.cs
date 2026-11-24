using System;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;

namespace LovenseConnector
{
    internal sealed partial class ConnectorApp
    {
        GlobalStopHotkey screenShortcut;
        ComboBox screenShortcutModifiers,screenShortcutKey;
        Button screenShortcutApply,screenShortcutDisable;
        TextBlock screenShortcutStatus;
        int screenShortcutRevision;
        bool screenShortcutStarting;

        void BuildScreenShortcutSettings(Panel parent)
        {
            var group=new StackPanel {Margin=new Thickness(0,12,0,0)};
            var note=Text("Start a preview first. Use this shortcut in the captured window to enable feedback.",13);note.TextWrapping=TextWrapping.Wrap;group.Children.Add(note);
            screenShortcutModifiers=new ComboBox {MinWidth=220,HorizontalAlignment=HorizontalAlignment.Left};
            foreach(string label in StopHotkeyChord.ModifierLabels)screenShortcutModifiers.Items.Add(label);
            screenShortcutModifiers.SelectedIndex=Array.IndexOf(StopHotkeyChord.ModifierChoices,studioUiPreferences.ScreenShortcutModifiers);
            Accessible(screenShortcutModifiers,"Enable Screen sync shortcut modifiers");group.Children.Add(StudioSettingRow("Modifiers",screenShortcutModifiers));
            screenShortcutKey=new ComboBox {MinWidth=220,HorizontalAlignment=HorizontalAlignment.Left};
            for(int key=6;key<=11;key++)screenShortcutKey.Items.Add("F"+key);
            screenShortcutKey.SelectedIndex=(int)studioUiPreferences.ScreenShortcutKey-0x75;
            Accessible(screenShortcutKey,"Enable Screen sync shortcut key");group.Children.Add(StudioSettingRow("Key",screenShortcutKey));
            var actions=new WrapPanel();
            screenShortcutApply=Action("Enable shortcut",()=>{ApplyScreenShortcut(true);return Task.CompletedTask;});
            Accessible(screenShortcutApply,"Enable or apply Screen sync shortcut");
            AutomationProperties.SetHelpText(screenShortcutApply,"Enables screen feedback only with a live, readable HUD in the selected foreground window.");actions.Children.Add(screenShortcutApply);
            screenShortcutDisable=Action("Disable",()=>{ApplyScreenShortcut(false);return Task.CompletedTask;});
            Accessible(screenShortcutDisable,"Disable Screen sync shortcut");actions.Children.Add(screenShortcutDisable);group.Children.Add(actions);
            screenShortcutStatus=Text("Off",13);screenShortcutStatus.Foreground=Color("#C4BAD7");AutomationProperties.SetLiveSetting(screenShortcutStatus,AutomationLiveSetting.Polite);group.Children.Add(screenShortcutStatus);
            parent.Children.Add(new Expander {Header="Enable Screen sync shortcut",Content=group,Margin=new Thickness(0,24,0,0),IsExpanded=studioUiPreferences.ScreenShortcutEnabled});
            RefreshScreenShortcutSettings();
        }
        void AttachScreenShortcut(IStopHotkeyRegistrar registrar)
        {
            CloseScreenShortcut();
            // Separate ID ranges prevent either action from consuming the other's messages.
            screenShortcut=new GlobalStopHotkey(registrar,()=>{if(!closing)_=EnableScreenFromShortcut();},0x8000,0xbfff);
            if(studioUiPreferences.ScreenShortcutEnabled&&!screenShortcut.Configure(true,new StopHotkeyChord(studioUiPreferences.ScreenShortcutModifiers,studioUiPreferences.ScreenShortcutKey)))
                studioUiPreferences.ScreenShortcutEnabled=false;
            RefreshScreenShortcutSettings();
        }
        async Task EnableScreenFromShortcut()
        {
            if(closing||screenShortcut?.Active!=true||screenShortcutStarting||screenArmed)return;
            var registration=screenShortcut;int revision=screenShortcutRevision;var capture=screenCapture;
            bool Current()=>!closing&&ReferenceEquals(registration,screenShortcut)&&registration.Active&&revision==screenShortcutRevision&&ReferenceEquals(capture,screenCapture)&&capture?.Foreground==true;
            if(capture==null){SetScreenStatus("Start a preview before using the shortcut");return;}
            if(!Current()){SetScreenStatus("Select the captured window before enabling output");return;}
            screenShortcutStarting=true;
            try {await ArmScreenCore(Current);}
            catch(Exception ex) {SetScreenStatus(ex.Message);}
            finally {screenShortcutStarting=false;}
        }
        void ApplyScreenShortcut(bool enabled)
        {
            screenShortcutRevision++;
            if(screenShortcut==null||closing)return;
            int index=screenShortcutModifiers.SelectedIndex;
            var chord=new StopHotkeyChord(index>=0?StopHotkeyChord.ModifierChoices[index]:0,(uint)(screenShortcutKey.SelectedIndex+0x75));
            screenShortcut.Configure(enabled,chord);
            studioUiPreferences.ScreenShortcutEnabled=screenShortcut.Active;
            var saved=screenShortcut.ActiveChord??(chord.Valid?chord:StopHotkeyChord.ScreenDefault);
            studioUiPreferences.ScreenShortcutModifiers=saved.Modifiers;studioUiPreferences.ScreenShortcutKey=saved.Key;
            SaveStudioUiPreferences();RefreshScreenShortcutSettings();
        }
        void RefreshScreenShortcutSettings()
        {
            if(screenShortcutStatus==null)return;
            screenShortcutApply.IsEnabled=screenShortcut!=null&&!closing;
            screenShortcutApply.Content=screenShortcut?.Active==true?"Apply shortcut":"Enable shortcut";
            screenShortcutDisable.IsEnabled=screenShortcut?.HasRegistrations==true&&!closing;
            screenShortcutStatus.Text=screenShortcut?.Active==true?"Active · "+screenShortcut.ActiveChord.Value.Label:
                screenShortcut==null&&studioUiPreferences.ScreenShortcutEnabled?"Waiting for window…":"Off";
            if(!string.IsNullOrEmpty(screenShortcut?.Error))screenShortcutStatus.Text+="\n"+screenShortcut.Error;
        }
        void CloseScreenShortcut()
        {
            screenShortcutRevision++;screenShortcut?.Dispose();screenShortcut=null;
        }
    }
}

using System;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Interop;

namespace LovenseConnector
{
    internal sealed partial class ConnectorApp
    {
        GlobalStopHotkey stopShortcut;
        WindowsStopHotkeyRegistrar stopShortcutWindow;
        ComboBox stopShortcutModifiers,stopShortcutKey;
        Button stopShortcutApply,stopShortcutDisable;
        TextBlock stopShortcutStatus;

        void BuildStopShortcutSettings(Panel parent)
        {
            var group=new StackPanel {Margin=new Thickness(0,24,0,0)};group.Children.Add(Text("Global Stop all",21));
            stopShortcutModifiers=new ComboBox {MinWidth=220,HorizontalAlignment=HorizontalAlignment.Left};
            foreach(var label in StopHotkeyChord.ModifierLabels)stopShortcutModifiers.Items.Add(label);
            stopShortcutModifiers.SelectedIndex=Array.IndexOf(StopHotkeyChord.ModifierChoices,studioUiPreferences.StopModifiers);
            Accessible(stopShortcutModifiers,"Global Stop all shortcut modifiers");group.Children.Add(StudioSettingRow("Modifiers",stopShortcutModifiers));
            stopShortcutKey=new ComboBox {MinWidth=220,HorizontalAlignment=HorizontalAlignment.Left};
            for(int key=6;key<=11;key++)stopShortcutKey.Items.Add("F"+key);
            stopShortcutKey.SelectedIndex=(int)studioUiPreferences.StopKey-0x75;
            Accessible(stopShortcutKey,"Global Stop all shortcut key");group.Children.Add(StudioSettingRow("Key",stopShortcutKey));
            var actions=new WrapPanel();
            stopShortcutApply=Action("Enable shortcut",()=>{ApplyStopShortcut(true);return Task.CompletedTask;});
            Accessible(stopShortcutApply,"Enable or apply global Stop all shortcut");
            AutomationProperties.SetHelpText(stopShortcutApply,"Stops all sources without switching apps. Enabling reserves this shortcut in Windows.");actions.Children.Add(stopShortcutApply);
            stopShortcutDisable=Action("Disable",()=>{ApplyStopShortcut(false);return Task.CompletedTask;},()=>stopShortcut?.HasRegistrations==true);
            Accessible(stopShortcutDisable,"Disable global Stop all shortcut");actions.Children.Add(stopShortcutDisable);group.Children.Add(actions);
            stopShortcutStatus=Text("Off",13);stopShortcutStatus.Foreground=Color("#C4BAD7");
            AutomationProperties.SetLiveSetting(stopShortcutStatus,AutomationLiveSetting.Polite);
            group.Children.Add(stopShortcutStatus);parent.Children.Add(group);RefreshStopShortcutSettings();
        }
        void InitializeStopShortcut()
        {
            window.SourceInitialized+=(s,e)=> {
                if(closing)return;
                stopShortcutWindow=new WindowsStopHotkeyRegistrar(HwndSource.FromHwnd(new WindowInteropHelper(window).Handle));
                AttachStopShortcut(stopShortcutWindow);stopShortcutWindow.Receive=HandleAppShortcut;
            };
            window.Closed+=(s,e)=>CloseStopShortcut();
        }
        void AttachStopShortcut(IStopHotkeyRegistrar registrar)
        {
            stopShortcut=new GlobalStopHotkey(registrar,()=>{if(!closing)_=StopFromShortcut();},0x4100,0x7fff);
            if(studioUiPreferences.StopEnabled&&!stopShortcut.Configure(true,new StopHotkeyChord(studioUiPreferences.StopModifiers,studioUiPreferences.StopKey)))
                studioUiPreferences.StopEnabled=false;
            RefreshStopShortcutSettings();
            AttachScreenShortcut(registrar);
        }
        bool HandleAppShortcut(int message,int id,long data)=>stopShortcut?.HandleMessage(message,id,data)==true||screenShortcut?.HandleMessage(message,id,data)==true;
        async Task StopFromShortcut()
        {
            try { await StopAll(); }
            catch(Exception ex) { Report(ex); }
        }
        void ApplyStopShortcut(bool enabled)
        {
            if(stopShortcut==null||closing)return;
            int index=stopShortcutModifiers.SelectedIndex;
            var chord=new StopHotkeyChord(index>=0?StopHotkeyChord.ModifierChoices[index]:0,(uint)(stopShortcutKey.SelectedIndex+0x75));
            stopShortcut.Configure(enabled,chord);
            studioUiPreferences.StopEnabled=stopShortcut.Active;
            var saved=stopShortcut.ActiveChord??(chord.Valid?chord:StopHotkeyChord.Default);
            studioUiPreferences.StopModifiers=saved.Modifiers;studioUiPreferences.StopKey=saved.Key;
            SaveStudioUiPreferences();RefreshStopShortcutSettings();
        }
        void RefreshStopShortcutSettings()
        {
            if(stopShortcutStatus==null)return;
            stopShortcutApply.IsEnabled=stopShortcut!=null&&!closing;
            stopShortcutApply.Content=stopShortcut?.Active==true?"Apply shortcut":"Enable shortcut";
            stopShortcutDisable.IsEnabled=stopShortcut?.HasRegistrations==true&&!closing;
            stopShortcutStatus.Text=stopShortcut?.Active==true?"Active · "+stopShortcut.ActiveChord.Value.Label:
                stopShortcut==null&&studioUiPreferences.StopEnabled?"Waiting for window…":"Off";
            if(!string.IsNullOrEmpty(stopShortcut?.Error))stopShortcutStatus.Text+="\n"+stopShortcut.Error;
        }
        void CloseStopShortcut()
        {
            CloseScreenShortcut();
            stopShortcut?.Dispose();stopShortcut=null;stopShortcutWindow?.Dispose();stopShortcutWindow=null;
        }
    }
}

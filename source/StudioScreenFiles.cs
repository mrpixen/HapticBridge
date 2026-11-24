using System;
using System.Threading.Tasks;
using System.Windows.Controls;
using Microsoft.Win32;

namespace LovenseConnector
{
    internal sealed partial class ConnectorApp
    {
        Button ScreenCalibrationFileMenu()
        {
            var menu=new ContextMenu();var button=Action("More…",()=>Task.CompletedTask);
            Accessible(button,"More calibration actions");
            void Item(string title,Func<Task> action)
            {
                var item=new MenuItem {Header=title};
                item.Click+=async(s,e)=>{try{await action();}catch(Exception ex){Report(ex);}};menu.Items.Add(item);
            }
            Item("Save setup…",ExportScreenCalibration);Item("Open setup…",OpenScreenCalibration);
            button.ContextMenu=menu;button.Click+=(s,e)=>{menu.PlacementTarget=button;menu.IsOpen=true;};return button;
        }
        async Task ExportScreenCalibration()
        {
            string text=ScreenCalibrationFiles.Encode(screenProfile);
            var stopped=StopAll();int operation=operatorGeneration;
            if(!await stopped||closing||operation!=operatorGeneration)return;
            var dialog=new SaveFileDialog {Filter="HapticBridge HUD setups|*.hud.json",FileName="My HUD.hud.json",DefaultExt=".hud.json",AddExtension=true};
            if(dialog.ShowDialog(window)!=true)return;
            ScreenCalibrationFiles.Save(dialog.FileName,text);SetScreenStatus("HUD setup saved");
        }
        async Task OpenScreenCalibration()
        {
            var stopped=StopAll();int operation=operatorGeneration;
            if(!await stopped||closing||operation!=operatorGeneration)return;
            var dialog=new OpenFileDialog {Filter="HapticBridge HUD setups|*.hud.json",Title="Open HUD setup"};
            if(dialog.ShowDialog(window)!=true)return;
            await ApplyScreenCalibration(ScreenCalibrationFiles.Load(dialog.FileName));
        }
        async Task ApplyScreenCalibration(ScreenCalibrationData data)
        {
            var incoming=data?.Snapshot()??throw new ArgumentException("Choose a valid HUD setup.");
            var stopped=DisarmScreen();int generation=screenGeneration;await stopped;
            if(closing||generation!=screenGeneration)return;
            RememberScreenCalibration();
            screenProfile.Health=incoming.Health;screenProfile.Shield=incoming.Shield;screenProfile.ReadShield=incoming.ReadShield;
            screenProfile.Badge=incoming.Badge;screenProfile.BadgeMask=incoming.BadgeMask;screenProfile.Digits=incoming.Digits;
            RefreshScreenCalibration();SetScreenStatus("Setup loaded · Save calibration to keep it");
        }
    }
}

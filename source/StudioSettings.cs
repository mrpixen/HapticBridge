using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace LovenseConnector
{
    internal sealed class StudioUiPreferences
    {
        public string Material { get; set; }="Glass";
        public string Motion { get; set; }="System";
        public bool StopEnabled { get; set; }
        public uint StopModifiers { get; set; }=StopHotkeyChord.Default.Modifiers;
        public uint StopKey { get; set; }=StopHotkeyChord.Default.Key;
    }
    internal sealed class StudioBrushState
    {
        public Brush Original,Applied;
    }

    internal sealed partial class ConnectorApp
    {
        StudioUiPreferences studioUiPreferences=new StudioUiPreferences();
        TabItem studioSettingsTab;
        ComboBox studioMaterialChoice,studioMotionChoice;
        TextBlock studioSettingsStatus;
        bool studioApplyingAppearance,studioAppearanceQueued;
        readonly Dictionary<DependencyObject,Dictionary<DependencyProperty,StudioBrushState>> studioOriginalBrushes=new Dictionary<DependencyObject,Dictionary<DependencyProperty,StudioBrushState>>();
        readonly Dictionary<UIElement,Visibility> studioOriginalVisibility=new Dictionary<UIElement,Visibility>();

        bool StudioMotionEnabled=>studioUiPreferences==null?SystemParameters.ClientAreaAnimation:
            studioUiPreferences.Motion=="Full"||(studioUiPreferences.Motion=="System"&&SystemParameters.ClientAreaAnimation);

        void QueueStudioAppearance()
        {
            if(studioUiPreferences==null||studioUiPreferences.Material!="Solid"||studioAppearanceQueued||window==null)return;
            studioAppearanceQueued=true;
            window.Dispatcher.BeginInvoke(new Action(()=> { studioAppearanceQueued=false;ApplyStudioAppearance(); }));
        }

        void InitializeStudioSettings()
        {
            if(studioSettingsTab!=null)return;
            try
            {
                string path=Path.Combine(dataDir,"ui-preferences.json");
                if(File.Exists(path)&&new FileInfo(path).Length<=4096)
                {
                    var loaded=json.Deserialize<StudioUiPreferences>(File.ReadAllText(path));
                    if(loaded!=null)
                    {
                        studioUiPreferences.Material=loaded.Material=="Solid"?"Solid":"Glass";
                        studioUiPreferences.Motion=new[]{"System","Reduced","Full"}.Contains(loaded.Motion)?loaded.Motion:"System";
                        var chord=new StopHotkeyChord(loaded.StopModifiers,loaded.StopKey);
                        studioUiPreferences.StopEnabled=loaded.StopEnabled&&chord.Valid;
                        studioUiPreferences.StopModifiers=chord.Valid?chord.Modifiers:StopHotkeyChord.Default.Modifiers;
                        studioUiPreferences.StopKey=chord.Valid?chord.Key:StopHotkeyChord.Default.Key;
                    }
                }
            }
            catch { /* A damaged appearance file must not prevent launching. */ }
            var body=new StackPanel { Margin=new Thickness(24) };
            body.Children.Add(new TextBlock { Text="Settings",FontSize=32,FontWeight=FontWeights.SemiBold,Margin=new Thickness(0,0,0,24) });
            var appearance=new StackPanel();var appearanceTitle=Text("Appearance",21);appearanceTitle.Margin=new Thickness(0,0,0,16);appearance.Children.Add(appearanceTitle);
            var material=new ComboBox { MinWidth=220,HorizontalAlignment=HorizontalAlignment.Left };
            studioMaterialChoice=material;
            material.Items.Add("Glass");material.Items.Add("Solid");material.SelectedItem=studioUiPreferences.Material;
            System.Windows.Automation.AutomationProperties.SetName(material,"Surface material");appearance.Children.Add(StudioSettingRow("Material",material));
            var motion=new ComboBox { MinWidth=220,HorizontalAlignment=HorizontalAlignment.Left };
            studioMotionChoice=motion;
            motion.Items.Add("Use Windows setting");motion.Items.Add("Reduced motion");motion.Items.Add("Full motion");motion.SelectedIndex=studioUiPreferences.Motion=="Reduced"?1:studioUiPreferences.Motion=="Full"?2:0;
            System.Windows.Automation.AutomationProperties.SetName(motion,"Interface motion");appearance.Children.Add(StudioSettingRow("Motion",motion));
            var appearanceCard=new Border { CornerRadius=new CornerRadius(24),Padding=new Thickness(22),Margin=new Thickness(0,0,0,12),BorderBrush=Color("#30E4D3FF"),BorderThickness=new Thickness(0,1,0,0),Child=appearance };appearanceCard.SetResourceReference(Border.BackgroundProperty,"Glass");body.Children.Add(appearanceCard);
            studioSettingsStatus=Text("",13);studioSettingsStatus.Visibility=Visibility.Collapsed;studioSettingsStatus.Foreground=Color("#C4BAD7");body.Children.Add(studioSettingsStatus);
            BuildStopShortcutSettings(body);

            var browserHelper=new StackPanel { Margin=new Thickness(0,24,0,0) };browserHelper.Children.Add(Text("Browser helper",21));
            var browserNote=Text("Save the helper, then load it in your browser. Instructions included.",13);browserNote.TextWrapping=TextWrapping.Wrap;browserNote.Foreground=Color("#C4BAD7");browserNote.Margin=new Thickness(0,8,0,8);browserHelper.Children.Add(browserNote);
            var browserActions=new WrapPanel();browserActions.Children.Add(Action("Save helper…",SaveBrowserHelper));browserHelper.Children.Add(browserActions);body.Children.Add(browserHelper);

            var diagnostics=new StackPanel { Margin=new Thickness(0,24,0,0) };diagnostics.Children.Add(Text("Diagnostics",21));
            var actions=new WrapPanel();
            actions.Children.Add(Action("Copy diagnostics",()=> { Clipboard.SetText(StudioDiagnosticSummary());studioSettingsStatus.Text="Copied app status without device names, IDs or access keys.";studioSettingsStatus.Visibility=Visibility.Visible;return System.Threading.Tasks.Task.CompletedTask; }));
            actions.Children.Add(Action("Open local folder",()=> { Process.Start(new ProcessStartInfo(dataDir) { UseShellExecute=true });return System.Threading.Tasks.Task.CompletedTask; }));
            actions.Children.Add(Action("Help and licenses",()=> { AssetFiles.ShowHelp(window);return System.Threading.Tasks.Task.CompletedTask; }));diagnostics.Children.Add(actions);body.Children.Add(diagnostics);
            studioSettingsTab=new TabItem { Header="Settings",Content=new ScrollViewer { Content=body,VerticalScrollBarVisibility=ScrollBarVisibility.Auto,HorizontalScrollBarVisibility=ScrollBarVisibility.Disabled } };tabs.Items.Add(studioSettingsTab);
            material.SelectionChanged+=(s,e)=> { if(studioApplyingAppearance)return;studioUiPreferences.Material=(string)material.SelectedItem;SaveStudioUiPreferences();ApplyStudioAppearance(); };
            motion.SelectionChanged+=(s,e)=> { if(studioApplyingAppearance)return;studioUiPreferences.Motion=motion.SelectedIndex==1?"Reduced":motion.SelectedIndex==2?"Full":"System";SaveStudioUiPreferences();ApplyStudioAppearance(); };
            tabs.SelectionChanged+=(s,e)=> { if(ReferenceEquals(e.Source,tabs))window.Dispatcher.BeginInvoke(new Action(ApplyStudioAppearance)); };
            window.ContentRendered+=(s,e)=>ApplyStudioAppearance();
            ApplyStudioAppearance();
        }

        async System.Threading.Tasks.Task SaveBrowserHelper()
        {
            studioSettingsStatus.Visibility=Visibility.Visible;
            try
            {
                if(!await StopAll())
                {
                    studioSettingsStatus.Text="Could not confirm Stop. Stop all output before saving the helper.";
                    return;
                }
                var dialog=new Microsoft.Win32.SaveFileDialog { Filter="Browser helper ZIP|*.zip",FileName="HapticBridge-Browser-Helper.zip",DefaultExt=".zip",AddExtension=true,OverwritePrompt=true };
                if(dialog.ShowDialog(window)!=true){studioSettingsStatus.Visibility=Visibility.Collapsed;return;}
                BrowserHelper.Export(dialog.FileName);
                studioSettingsStatus.Text="Saved. Extract the ZIP and follow README.txt to load it in your browser.";
            }
            catch { studioSettingsStatus.Text="Could not save the browser helper. Choose a writable folder and try again."; }
        }

        static Grid StudioSettingRow(string title,FrameworkElement control)
        {
            var row=new Grid { Margin=new Thickness(0,0,0,12) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width=new GridLength(130) });row.ColumnDefinitions.Add(new ColumnDefinition());
            row.Children.Add(new TextBlock { Text=title,FontSize=15,VerticalAlignment=VerticalAlignment.Center });Grid.SetColumn(control,1);row.Children.Add(control);
            return row;
        }

        void SaveStudioUiPreferences()
        {
            studioSettingsStatus.Visibility=Visibility.Visible;
            try
            {
                string path=Path.Combine(dataDir,"ui-preferences.json"),temporary=path+".tmp";
                File.WriteAllText(temporary,json.Serialize(studioUiPreferences));
                if(File.Exists(path))File.Replace(temporary,path,null);else File.Move(temporary,path);
                studioSettingsStatus.Text="Saved.";
            }
            catch { studioSettingsStatus.Text="Could not save settings. Changes apply for this session."; }
        }

        string StudioDiagnosticSummary()
        {
            var result=new StringBuilder();
            result.AppendLine("HapticBridge "+Assembly.GetExecutingAssembly().GetName().Version);
            result.AppendLine("Runtime: "+Environment.Version);
            result.AppendLine("Bluetooth available: "+ready);
            result.AppendLine("Scanning: "+scanning);
            result.AppendLine("Connected devices: "+devices.Values.Count(d=>d.Connected));
            result.AppendLine("Supported motor channels: "+devices.Values.Where(d=>d.Connected).Sum(d=>d.Channels.Length));
            result.AppendLine("Local integrations enabled: "+apiEnabled);
            result.AppendLine("Sound capture active: "+(audioCapture!=null));
            result.AppendLine("Video output enabled: "+feelArmed);
            result.AppendLine("Screen capture active: "+(screenCapture!=null));
            result.AppendLine("Screen output enabled: "+screenArmed);
            result.AppendLine("Screen preview frozen: "+screenFrozen);
            result.AppendLine("Screen frame: "+(screenFrame==null?"none":screenFrozen?"frozen":screenCapture!=null&&ScreenCapture.Now-screenFrame.Time<=.75?"fresh":"stale"));
            result.AppendLine("Screen shield reading enabled: "+(screenProfile?.ReadShield??false));
            result.AppendLine("Screen learned digit templates present: "+(screenProfile?.Digits?.Count>0));
            result.AppendLine("Screen learned kill badge present: "+(screenProfile?.BadgeMask!=null));
            result.AppendLine("Screen selected connected motors: "+screenRoutes.Values.Count(r=>r.Include.IsChecked==true&&r.Device.Connected&&r.Device.Player!=null));
            result.AppendLine("Material: "+studioUiPreferences.Material);
            result.AppendLine("Motion: "+studioUiPreferences.Motion);
            return result.ToString();
        }

        void ApplyStudioAppearance()
        {
            if(studioApplyingAppearance||window==null)return;
            studioApplyingAppearance=true;
            try
            {
                bool solid=studioUiPreferences.Material=="Solid";
                if(studioMaterialChoice!=null)studioMaterialChoice.SelectedItem=studioUiPreferences.Material;
                if(studioMotionChoice!=null)studioMotionChoice.SelectedIndex=studioUiPreferences.Motion=="Reduced"?1:studioUiPreferences.Motion=="Full"?2:0;
                if(!solid)
                {
                    foreach(var item in studioOriginalBrushes)foreach(var property in item.Value)
                        if(ReferenceEquals(item.Key.GetValue(property.Key),property.Value.Applied))item.Key.SetCurrentValue(property.Key,property.Value.Original);
                    studioOriginalBrushes.Clear();
                    foreach(var item in studioOriginalVisibility)item.Key.Visibility=item.Value;
                    studioOriginalVisibility.Clear();
                }
                foreach(string name in new[]{"AmbientGlow1","AmbientGlow2"})
                    if(window.FindName(name) is UIElement glow&&solid)
                    {
                        if(!studioOriginalVisibility.ContainsKey(glow))studioOriginalVisibility[glow]=glow.Visibility;
                        glow.Visibility=Visibility.Collapsed;
                    }
                var seen=new HashSet<DependencyObject>();
                ApplyStudioElement(window,solid,!StudioMotionEnabled,seen);
            }
            finally { studioApplyingAppearance=false; }
        }

        void ApplyStudioElement(DependencyObject element,bool solid,bool reduceMotion,HashSet<DependencyObject> seen)
        {
            if(element==null||!seen.Add(element))return;
            if(solid)
            {
                if(element is Panel)MakeStudioBrushOpaque(element,Panel.BackgroundProperty);
                if(element is Border)MakeStudioBrushOpaque(element,Border.BackgroundProperty);
                if(element is Control)MakeStudioBrushOpaque(element,Control.BackgroundProperty);
                if(element is Window)MakeStudioBrushOpaque(element,Window.BackgroundProperty);
            }
            if(reduceMotion&&element is Border glimmer&&glimmer.Name=="Glimmer")
            {
                glimmer.BeginAnimation(UIElement.OpacityProperty,null);glimmer.Opacity=0;
                if(glimmer.Background is RadialGradientBrush brush)
                {
                    brush.BeginAnimation(RadialGradientBrush.CenterProperty,null);
                    brush.BeginAnimation(RadialGradientBrush.GradientOriginProperty,null);
                }
            }
            foreach(object child in LogicalTreeHelper.GetChildren(element))if(child is DependencyObject dependency)ApplyStudioElement(dependency,solid,reduceMotion,seen);
            if(element is Visual||element is System.Windows.Media.Media3D.Visual3D)
                for(int i=0;i<VisualTreeHelper.GetChildrenCount(element);i++)ApplyStudioElement(VisualTreeHelper.GetChild(element,i),solid,reduceMotion,seen);
        }

        void MakeStudioBrushOpaque(DependencyObject element,DependencyProperty property)
        {
            var brush=element.GetValue(property) as Brush;
            if(brush==null)return;
            if(element is FrameworkElement framework&&framework.Name=="Glimmer")return;
            System.Windows.Media.Color color;
            if(brush is SolidColorBrush simple)
            {
                if(simple.Color.A==0||(simple.Color.A==255&&brush.Opacity>=1))return;
                color=simple.Color;
            }
            else if(brush is GradientBrush gradient&&gradient.GradientStops.Count>0)
            {
                var stops=gradient.GradientStops.OrderBy(stop=>stop.Offset).ToArray();
                color=stops.OrderBy(stop=>Math.Abs(stop.Offset-.5)).First().Color;
            }
            else return;
            if(!studioOriginalBrushes.TryGetValue(element,out var properties))studioOriginalBrushes[element]=properties=new Dictionary<DependencyProperty,StudioBrushState>();
            if(!properties.TryGetValue(property,out var saved))properties[property]=saved=new StudioBrushState { Original=brush };
            else if(!ReferenceEquals(brush,saved.Applied))saved.Original=brush;
            double alpha=color.A/255.0*brush.Opacity;
            var opaque=System.Windows.Media.Color.FromRgb((byte)Math.Round(color.R*alpha+20*(1-alpha)),(byte)Math.Round(color.G*alpha+16*(1-alpha)),(byte)Math.Round(color.B*alpha+36*(1-alpha)));
            var replacement=new SolidColorBrush(opaque);replacement.Freeze();saved.Applied=replacement;element.SetCurrentValue(property,replacement);
        }
    }
}

using System;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;

namespace LovenseConnector
{
    internal sealed partial class ConnectorApp
    {
        ComboBox audioOutputChoice;
        ComboBox audioSourceChoice,audioProgramChoice;
        Func<AudioProgram[]> audioProgramProvider=null;
        Func<AudioProgram,IAudioSource> audioProgramFactory=null;
        Func<bool> audioProgramSupport=null;
        bool ProgramAudioSupported => (audioProgramSupport??(()=>WindowsProcessAudio.Supported))();
        bool audioOutputsLoading, audioOutputsLoaded;
        Func<AudioOutput[]> audioOutputProvider=null;
        Func<string,IAudioSource> audioSourceFactory=null;

        void InitializeStudioAudio()
        {
            var body=new StackPanel { Margin=new Thickness(24) };body.Children.Add(Text("Sound",32));
            audioSourceChoice=new ComboBox { Width=190,HorizontalAlignment=HorizontalAlignment.Left,Margin=new Thickness(0,0,0,12) };
            audioSourceChoice.Items.Add("Audio output");audioSourceChoice.Items.Add("One program");audioSourceChoice.SelectedIndex=0;
            Accessible(audioSourceChoice,"Sound source");body.Children.Add(audioSourceChoice);
            var programPanel=new StackPanel { Visibility=Visibility.Collapsed };
            audioProgramChoice=new ComboBox { MinWidth=160,MaxWidth=480,DisplayMemberPath="Name",HorizontalAlignment=HorizontalAlignment.Stretch };
            Accessible(audioProgramChoice,"Program to follow");programPanel.Children.Add(audioProgramChoice);
            programPanel.Children.Add(Action("Refresh programs",RefreshAudioPrograms));
            programPanel.Children.Add(Text("Includes the selected program and its child processes. Windows build 20348 or later.",13));
            body.Children.Add(programPanel);
            var source=new StackPanel();source.Children.Add(Text("Audio output",18));
            var outputRow=new Grid();outputRow.ColumnDefinitions.Add(new ColumnDefinition());outputRow.ColumnDefinitions.Add(new ColumnDefinition { Width=GridLength.Auto });
            audioOutputChoice=new ComboBox { MinWidth=160,MaxWidth=480,HorizontalAlignment=HorizontalAlignment.Stretch,DisplayMemberPath="Name",SelectedValuePath="Id",Margin=new Thickness(0,0,12,6) };
            Accessible(audioOutputChoice,"Audio output to follow");outputRow.Children.Add(audioOutputChoice);
            var refresh=Action("Refresh",RefreshAudioOutputs);Grid.SetColumn(refresh,1);outputRow.Children.Add(refresh);source.Children.Add(outputRow);
            source.Children.Add(Text("Follows all sound playing through this output.",13));var outputPanel=StudioPanel(source);body.Children.Add(outputPanel);
            audioSourceChoice.SelectionChanged+=async(s,e)=>{
                bool program=audioSourceChoice.SelectedIndex==1;
                programPanel.Visibility=program?Visibility.Visible:Visibility.Collapsed;
                outputPanel.Visibility=program?Visibility.Collapsed:Visibility.Visible;
                try { await AudioOutputChanged();if(program&&audioSourceChoice.SelectedIndex==1)await RefreshAudioPrograms(); }catch(Exception ex){Report(ex);}
            };
            audioProgramChoice.SelectionChanged+=async(s,e)=>{if(!audioOutputsLoading)try{await AudioOutputChanged();}catch(Exception ex){Report(ex);}};
            audioOutputsLoading=true;audioOutputChoice.Items.Add(new AudioOutput { Id="",Name="Windows default" });audioOutputChoice.SelectedIndex=0;audioOutputsLoading=false;
            audioOutputChoice.SelectionChanged+=async(s,e)=>{if(!audioOutputsLoading)try{await AudioOutputChanged();}catch(Exception ex){Report(ex);}};
            body.Loaded+=async(s,e)=>{if(!audioOutputsLoaded){audioOutputsLoaded=true;await RefreshAudioOutputs();}};

            var options=new WrapPanel { Margin=new Thickness(0,6,0,12) };audioMode=new ComboBox { Width=190 };audioMode.Items.Add("Bass rumble");audioMode.Items.Add("Full sound");audioMode.SelectedIndex=0;Field(options,"Response",audioMode);
            audioGain=new Slider { Minimum=1,Maximum=12,Value=4,Width=220,TickFrequency=1,IsSnapToTickEnabled=true };Accessible(audioGain,"Sound sensitivity");Field(options,"Sensitivity",audioGain);body.Children.Add(options);
            var actions=new WrapPanel();actions.Children.Add(Action("Start sound sync",StartSoundSync));actions.Children.Add(Action("Stop sound sync",StopAudioOutput));body.Children.Add(actions);
            audioLabel=Text("Not listening",15);body.Children.Add(audioLabel);body.Children.Add(Text("Motors · maximum level",18));studioAudioRoutes=new WrapPanel();body.Children.Add(studioAudioRoutes);
            tabs.Items.Insert(1,new TabItem { Header="Sound",Content=new ScrollViewer { Content=body,VerticalScrollBarVisibility=ScrollBarVisibility.Auto,HorizontalScrollBarVisibility=ScrollBarVisibility.Disabled } });
        }
        async Task StartSoundSync()
        {
            int request=++audioStartRequest;
            await Manual(async()=>{if(request==audioStartRequest)await StartAudio();});
        }
        async Task RefreshAudioPrograms()
        {
            audioStartRequest++;
            var selected=audioProgramChoice.SelectedItem as AudioProgram;
            var stopped=StopAudioMotors();int generation=audioGeneration;
            await stopped;
            if(generation!=audioGeneration||audioCapture!=null)return;
            try {
                if(!ProgramAudioSupported) {
                    audioOutputsLoading=true;
                    try {audioProgramChoice.Items.Clear();}finally {audioOutputsLoading=false;}
                    audioLabel.Text=AudioStoppedMessage("Program audio needs a newer Windows version · use Audio output instead");return;
                }
                var programs=(audioProgramProvider??AudioProgram.List)();
                audioOutputsLoading=true;
                try {
                    audioProgramChoice.Items.Clear();foreach(var program in programs)audioProgramChoice.Items.Add(program);
                    audioProgramChoice.SelectedItem=programs.FirstOrDefault(p=>selected!=null&&p.Id==selected.Id&&p.Started==selected.Started);
                    audioLabel.Text=AudioStoppedMessage(programs.Length==0?"No programs found · open a program and refresh":"Choose a program · press Start sound sync");
                }finally {audioOutputsLoading=false;}
            }catch {audioLabel.Text=AudioStoppedMessage("Could not list programs · try Refresh programs");}
        }
        async Task AudioOutputChanged()
        {
            // Invalidate synchronously, including an in-flight Start awaiting a stop.
            audioStartRequest++;
            var stopped=StopAudioMotors();int generation=audioGeneration;
            await stopped;
            if(generation==audioGeneration)audioLabel.Text=AudioStoppedMessage("Output changed · press Start sound sync");
        }
        async Task RefreshAudioOutputs()
        {
            audioStartRequest++;
            var selected=(audioOutputChoice.SelectedItem as AudioOutput)?.Id??"";
            try {
                var stopped=StopAudioMotors();int generation=audioGeneration;
                await stopped;
                if(generation!=audioGeneration||audioCapture!=null)return;
                var outputs=(audioOutputProvider??AudioSync.ListOutputs)();
                audioOutputsLoading=true;
                try {
                    audioOutputChoice.Items.Clear();audioOutputChoice.Items.Add(new AudioOutput { Id="",Name="Windows default" });
                    foreach(var output in outputs)audioOutputChoice.Items.Add(output);
                    var retained=audioOutputChoice.Items.Cast<AudioOutput>().FirstOrDefault(o=>o.Id==selected);
                    audioOutputChoice.SelectedItem=retained??audioOutputChoice.Items[0];
                    audioLabel.Text=AudioStoppedMessage(retained==null?"Previous output unavailable · Windows default selected · output stopped":outputs.Length==0?"No audio outputs found · connect one and refresh":"Ready · press Start sound sync");
                }finally{audioOutputsLoading=false;}
            }catch { audioLabel.Text=AudioStoppedMessage("Could not refresh audio outputs. Reconnect your audio device and try again."); }
        }
    }
}

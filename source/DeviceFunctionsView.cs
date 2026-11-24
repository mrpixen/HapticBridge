using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;

namespace LovenseConnector
{
    internal sealed partial class ConnectorApp
    {
        void BuildFunctionSettings(Device d)
        {
            var f=d.Functions;if(f==null)return;
            if(!string.IsNullOrEmpty(f.Note))d.Controls.Children.Add(Text(f.Note,12));
            if(!f.HasRotation&&!f.HasDepth&&!f.HasPosition)return;
            var panel=new StackPanel { Margin=new Thickness(4,10,4,12) };
            if(f.HasRotation)panel.Children.Add(Action("Reverse rotation",()=>ManualDevice(d,(p,current)=>p.ApplyFunction("reverse",0,isCurrent:current))));
            if(f.HasDepth) {
                var row=new WrapPanel();var depth=new ComboBox { Width=100,SelectedIndex=1 };foreach(int n in new[]{0,1,2,3})depth.Items.Add(n);
                Field(row,"Depth",depth);row.Children.Add(Action("Apply depth",()=>ManualDevice(d,(p,current)=>p.ApplyFunction("depth",(int)depth.SelectedItem,isCurrent:current))));panel.Children.Add(row);
            }
            if(f.HasStroke) {
                var row=new WrapPanel();var low=Input("");var high=Input("");
                low.SetBinding(TextBox.TextProperty,new System.Windows.Data.Binding(nameof(ActuatorProfile.StrokeLow)) { Source=f,Mode=System.Windows.Data.BindingMode.OneWay });
                high.SetBinding(TextBox.TextProperty,new System.Windows.Data.Binding(nameof(ActuatorProfile.StrokeHigh)) { Source=f,Mode=System.Windows.Data.BindingMode.OneWay });
                Field(row,"Stroke start %",low);Field(row,"Stroke end %",high);
                row.Children.Add(Action("Set stroke",()=>ManualDevice(d,(p,current)=>p.ApplyFunction("stroke",Number(low),Number(high),current))));panel.Children.Add(row);
            }
            if(f.HasPosition) {
                var row=new WrapPanel();var position=Input("0");Field(row,"Position %",position);
                row.Children.Add(Action("Move to position",()=>ManualDevice(d,(p,current)=>p.ApplyFunction("position",Number(position),isCurrent:current))));panel.Children.Add(row);
            }
            panel.Children.Add(Text("Changing a motion setting stops current playback. Start again when ready.",12));
            d.Controls.Children.Add(new Expander { Header="Motion settings",Content=panel,Margin=new Thickness(0,8,0,12) });
        }
    }
}

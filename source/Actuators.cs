using System;
using System.Collections.Generic;
using System.Linq;

namespace LovenseConnector
{
    // Channel numbers are stable integration IDs, not positions in an array.
    // Patterns retain their 0–20 scale; NativeMaximum describes device resolution.
    internal sealed class Actuator
    {
        public int Channel { get; }
        public string Function { get; }
        public string Label { get; }
        public int NativeMaximum { get; }
        readonly Func<int,string> encode;
        public Actuator(int channel,string function,string label,Func<int,string> encode,int nativeMaximum=20)
        { Channel=channel;Function=function;Label=label;this.encode=encode;NativeMaximum=nativeMaximum; }
        public string Command(int level)
        {
            if(level<0||level>20)throw new ArgumentOutOfRangeException(nameof(level));
            int native=level==0?0:Math.Max(1,(int)Math.Round(level*NativeMaximum/20.0,MidpointRounding.AwayFromZero));
            return encode(native);
        }
    }

    internal sealed class ActuatorProfile
    {
        public Actuator[] Outputs { get; private set; }
        public bool SupportsPresets=>Outputs.All(a=>a.Function=="vibration");
        public bool HasDepth { get; private set; }
        public bool HasPosition { get; private set; }
        public bool HasStroke => HasPosition;
        public bool HasRotation => Outputs.Any(a=>a.Function=="rotation");
        public string Note { get; private set; }
        // The immutable pair is replaced atomically. Sampling never sees half a range.
        Tuple<int,int> stroke=Tuple.Create(0,100);
        public Tuple<int,int> StrokeRange=>stroke;
        public int StrokeLow=>stroke.Item1;
        public int StrokeHigh=>stroke.Item2;
        public static ActuatorProfile Create(ModelMatch model,string symbol,string firmware)
        {
            var p=new ActuatorProfile();var result=new List<Actuator>();
            var features=model.Features??Array.Empty<string>();
            bool Has(string feature)=>features.Contains(feature);
            void Add(int channel,string function,string label,string command,int maximum=20)=>
                result.Add(new Actuator(channel,function,label,n=>command+n+";",maximum));
            string id=(symbol??"").ToUpperInvariant();
            bool machine=id=="F"||id=="FS";
            bool suction=Has("Suction");
            bool flexer=id=="EI";
            bool independentFlex=flexer&&int.TryParse(firmware,out int version)&&version>=3;
            bool dual=id=="EA"||id=="WD"||id=="OC"&&Has("Oscillation");
            if(independentFlex) {
                result.Add(new Actuator(1,"vibration","Vibration 1",n=>"Mply:"+n+":-1:-1;"));
                result.Add(new Actuator(2,"vibration","Vibration 2",n=>"Mply:-1:"+n+":-1;"));
                result.Add(new Actuator(6,"flexing","Flexing",n=>"Mply:-1:-1:"+n+";"));
            } else if(Has("Vibration")&&!machine&&!suction) {
                foreach(int c in id=="EB"?new[]{1,2}:Catalog.VibrationChannels(model)) {
                    if(id=="U") {
                        int slot=c;result.Add(new Actuator(c,"vibration","Vibration "+c,n=>"Mply:"+string.Join(":",Enumerable.Range(1,3).Select(i=>i==slot?n:-1))+";"));
                    } else Add(c,"vibration",c==0?"Vibration":"Vibration "+c,dual?"Vibrate1:":c==0?"Vibrate:":"Vibrate"+c+":");
                }
            }
            if(flexer&&!independentFlex)p.Note="Independent Flexer controls require firmware 3 or later.";
            if(Has("Rotation"))Add(4,"rotation","Rotation","Rotate:");
            if(Has("Air pump"))Add(5,"pump","Air pump","Air:Level:",3);
            if(Has("Flexing")&&!flexer)Add(6,"flexing","Flexing","Finger:");
            if(Has("Thrusting")) {
                if(id=="BA")result.Add(new Actuator(7,"thrusting","Thrusting",n=>n==0?"Mply:0:20;":p.StrokeCommand(n)));
                else if(id=="H")result.Add(new Actuator(7,"thrusting","Thrusting",n=>n==0?"Mply:0:0;":"Thrusting:"+n+";"));
                else Add(7,"thrusting","Thrusting",machine?"Vibrate:":dual?"Vibrate2:":"Thrusting:");
            }
            // These models' vibration API is an alias for their suction actuator.
            if(suction)Add(8,"suction","Suction","Vibrate:");
            if(Has("Oscillation"))Add(9,"oscillation","Oscillation",dual?"Vibrate2:":id=="EZ"?"Vibrate:":"Slap:");
            p.HasDepth=Has("Depth");p.HasPosition=Has("Position");p.Outputs=result.ToArray();return p;
        }
        public Actuator Get(int channel)=>Outputs.FirstOrDefault(a=>a.Channel==channel)??throw new ArgumentException("Unsupported actuator channel.");
        public string Command(int channel,int level)=>Get(channel).Command(level);
        public void SetStroke(int low,int high)
        {
            if(!HasStroke)throw new NotSupportedException("Stroke range is not supported by this model.");
            if(low<0||high>100||high-low<20)throw new ArgumentException("Stroke endpoints must be 0–100 and at least 20 apart.");
            stroke=Tuple.Create(low,high);
        }
        string StrokeCommand(int speed)
        {
            var range=stroke;
            // Windows BLE SDK v1.2: FLVS: followed by three raw bytes, not decimal text.
            return "FLVS:"+(char)speed+(char)range.Item1+(char)range.Item2+";";
        }
        public string DepthCommand(int depth)
        {
            if(!HasDepth)throw new NotSupportedException("Depth is not supported by this model.");
            if(depth<0||depth>3)throw new ArgumentOutOfRangeException(nameof(depth));
            return "Depth:"+depth+";";
        }
        public string PositionCommand(int position)
        {
            if(!HasPosition)throw new NotSupportedException("Position is not supported by this model.");
            if(position<0||position>100)throw new ArgumentOutOfRangeException(nameof(position));
            return "FSetSite:"+position+";";
        }
    }
}

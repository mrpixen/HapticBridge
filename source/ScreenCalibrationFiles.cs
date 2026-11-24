using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Web.Script.Serialization;

namespace LovenseConnector
{
    // Export only recognition settings, never motor routes, strengths or runtime state.
    internal sealed class ScreenCalibrationData
    {
        public string Format { get; set; }
        public int Version { get; set; }
        public HudRegion Health { get; set; }
        public HudRegion Shield { get; set; }
        public bool ReadShield { get; set; }
        public HudRegion Badge { get; set; }
        public byte[] BadgeMask { get; set; }
        public List<byte[]> BadgeVariants { get; set; }=new List<byte[]>();
        public List<HudGlyph> Digits { get; set; }

        public bool Valid()=>Format=="hapticbridge-hud"&&(Version==1||Version==2)&&BadgeVariants!=null&&(Version==2||BadgeVariants.Count==0)&&new ScreenProfile {
            Health=Health,Shield=Shield,ReadShield=ReadShield,Badge=Badge,BadgeMask=BadgeMask,BadgeVariants=BadgeVariants,Digits=Digits
        }.Validate();
        static HudRegion Copy(HudRegion r)=>new HudRegion {X=r.X,Y=r.Y,Width=r.Width,Height=r.Height};
        public static ScreenCalibrationData Capture(ScreenProfile profile)
        {
            if(profile==null||!profile.Validate())throw new ArgumentException("Invalid HUD calibration.");
            return new ScreenCalibrationData {
                Format="hapticbridge-hud",Version=2,Health=Copy(profile.Health),Shield=Copy(profile.Shield),ReadShield=profile.ReadShield,
                Badge=Copy(profile.Badge),BadgeMask=profile.BadgeMask?.ToArray(),
                BadgeVariants=profile.BadgeVariants.Select(m=>m.ToArray()).ToList(),
                Digits=profile.Digits.Select(d=>new HudGlyph {Digit=d.Digit,Aspect=d.Aspect,Mask=d.Mask.ToArray()}).ToList()
            };
        }
        public ScreenProfile Snapshot()
        {
            if(!Valid())throw new ArgumentException("This is not a valid HapticBridge HUD setup.");
            var copy=Capture(new ScreenProfile {Health=Health,Shield=Shield,ReadShield=ReadShield,Badge=Badge,BadgeMask=BadgeMask,BadgeVariants=BadgeVariants,Digits=Digits});
            return new ScreenProfile {Health=copy.Health,Shield=copy.Shield,ReadShield=copy.ReadShield,Badge=copy.Badge,BadgeMask=copy.BadgeMask,BadgeVariants=copy.BadgeVariants,Digits=copy.Digits};
        }
    }

    internal static class ScreenCalibrationFiles
    {
        internal const int MaximumBytes=150000;
        static readonly JavaScriptSerializer Json=new JavaScriptSerializer();
        internal static string Encode(ScreenProfile profile)
        {
            string text=Json.Serialize(ScreenCalibrationData.Capture(profile));
            if(Encoding.UTF8.GetByteCount(text)>MaximumBytes)throw new ArgumentException("HUD setup is too large.");
            return text;
        }
        internal static ScreenCalibrationData Decode(string text)
        {
            if(text==null||text.Length>MaximumBytes||Encoding.UTF8.GetByteCount(text)>MaximumBytes)throw new ArgumentException("HUD setup is too large.");
            var data=Json.Deserialize<ScreenCalibrationData>(text);
            if(data==null||!data.Valid())throw new ArgumentException("This is not a valid HapticBridge HUD setup.");
            return data;
        }
        internal static ScreenCalibrationData Load(string path)
        {
            using var stream=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.Read);
            if(stream.Length>MaximumBytes)throw new ArgumentException("HUD setup is too large.");
            using var reader=new StreamReader(stream,Encoding.UTF8,true);
            var buffer=new char[MaximumBytes+1];int count=reader.ReadBlock(buffer,0,buffer.Length);
            if(count>MaximumBytes)throw new ArgumentException("HUD setup is too large.");
            return Decode(new string(buffer,0,count));
        }
        internal static void Save(string path,string text)
        {
            Decode(text);
            string target=Path.GetFullPath(path),temporary=target+"."+Guid.NewGuid().ToString("N")+".tmp";
            try { File.WriteAllText(temporary,text);File.Move(temporary,target,true); }
            finally {try{if(File.Exists(temporary))File.Delete(temporary);}catch(IOException){}catch(UnauthorizedAccessException){}}
        }
    }
}

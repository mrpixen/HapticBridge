using System;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;

namespace LovenseConnector
{
    internal static class Replies
    {
        public static bool IsError(string value) { return value.StartsWith("ERR",StringComparison.OrdinalIgnoreCase)||value.StartsWith("unk",StringComparison.OrdinalIgnoreCase); }
        public static bool CommandMatches(string command,string value,bool edgeMotorEcho)
        {
            if(value=="OK") return true;
            // Edge 2 firmware 204 returns e.g. Vibrate1:2;; instead of OK;.
            // Accept only an exact, in-range motor-command echo for a known Edge.
            return edgeMotorEcho && Regex.IsMatch(command,@"^Vibrate[12]:(?:[0-9]|1[0-9]|20);$") && value==command.TrimEnd(';');
        }
    }
    internal sealed class ReplyFramer
    {
        readonly StringBuilder buffer=new StringBuilder();
        public string[] Append(string fragment)
        {
            buffer.Append(fragment); var lines=new List<string>(); string all=buffer.ToString(); int index;
            while((index=all.IndexOf(';'))>=0) { if(index>0) lines.Add(all.Substring(0,index)); all=all.Substring(index+1); }
            buffer.Clear(); if(all.Length>4096) throw new InvalidOperationException("Toy response exceeded the frame limit."); buffer.Append(all); return lines.ToArray();
        }
    }
}

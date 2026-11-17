using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace LovenseConnector
{
    internal sealed record FeelOrderedTarget(Playback Player, OrderedMotorRoute Route);
    // The transport thread can submit without queuing every sample on WPF's
    // dispatcher. Only immutable, UI-published route snapshots cross threads.
    internal sealed class FeelOrderedRouter
    {
        readonly object sync=new();
        FeelOrderedTarget[] targets=Array.Empty<FeelOrderedTarget>();
        readonly HashSet<Playback> usedPlayers=new();
        OrderedOutputOwner owner=new();
        bool enabled;
        public void Publish(IEnumerable<FeelOrderedTarget> value) { lock(sync)targets=value.ToArray(); }
        public void Enable() { lock(sync) { owner=new();enabled=true; } }
        public Task Suppress(Playback player,int channel)
        {
            OrderedOutputOwner current;lock(sync)current=owner;
            return player.CancelOrdered(current,channel);
        }
        public void Submit(IReadOnlyList<double> samples)
        {
            lock(sync)
            {
                if(!enabled)return;
                foreach(var group in targets.GroupBy(t=>t.Player))
                {
                    usedPlayers.Add(group.Key);
                    group.Key.EnqueueOrdered(owner,samples,group.Select(t=>t.Route).ToArray());
                }
            }
        }
        public Task Reset(bool disable=false)
        {
            OrderedOutputOwner old;Playback[] players;
            lock(sync)
            {
                old=owner;old.Close();owner=new();if(disable)enabled=false;
                players=usedPlayers.ToArray();usedPlayers.Clear();
            }
            return Task.WhenAll(players.Select(p=>p.CancelOrdered(old)));
        }
    }
}

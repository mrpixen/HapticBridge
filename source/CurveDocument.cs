using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;

namespace LovenseConnector
{
    public enum CurveInterpolation { Step, Linear, Smooth }

    /// <summary>An immutable native vibration level at a millisecond offset.</summary>
    public readonly struct CurvePoint : IEquatable<CurvePoint>
    {
        public int Ms { get; }
        public int Level { get; }
        public CurvePoint(int ms, int level)
        {
            if (ms < 0 || ms > CurveDocument.MaximumLengthMs) throw new ArgumentOutOfRangeException(nameof(ms));
            if (level < 0 || level > 20) throw new ArgumentOutOfRangeException(nameof(level));
            Ms = ms; Level = level;
        }
        public bool Equals(CurvePoint other) => Ms == other.Ms && Level == other.Level;
        public override bool Equals(object other) => other is CurvePoint point && Equals(point);
        public override int GetHashCode() => HashCode.Combine(Ms, Level);
        public override string ToString() => Ms + " ms / " + Level;
    }

    internal sealed class CurveState
    {
        public string Id;
        public ReadOnlyCollection<CurvePoint> Points;
        public CurveInterpolation Interpolation;
        public CurveState(string id, IEnumerable<CurvePoint> points, CurveInterpolation interpolation)
        { Id = id; Points = Array.AsReadOnly(points.ToArray()); Interpolation = interpolation; }
        public CurveState Copy() => new CurveState(Id, Points, Interpolation);
    }

    public sealed class CurveLaneModel
    {
        internal CurveState State;
        public string Id { get; }
        public string DisplayName { get; internal set; }
        public bool IsAvailable { get; internal set; } = true;
        public IReadOnlyList<CurvePoint> Points => State.Points;
        public CurveInterpolation Interpolation => State.Interpolation;
        public string GroupId => State.Id;
        internal CurveLaneModel(string id, string name, CurveState state)
        { Id = id; DisplayName = name; State = state; }
    }

    /// <summary>
    /// UI-thread-owned, platform-neutral curve document. It never starts hardware.
    /// All graph values are native integer levels. One edit transaction is one Undo.
    /// Device topology changes deliberately clear history; availability does not.
    /// </summary>
    public sealed class CurveDocument
    {
        public const int MinimumLengthMs = 200, MaximumLengthMs = 600000;
        public const int MinimumPointSpacingMs = 100, MaximumPoints = 512;
        const int HistoryLimit = 100;
        readonly List<CurveLaneModel> lanes = new List<CurveLaneModel>();
        readonly Dictionary<string, CurveLaneModel> byId = new Dictionary<string, CurveLaneModel>(StringComparer.Ordinal);
        readonly List<Snapshot> undo = new List<Snapshot>(), redo = new List<Snapshot>();
        Snapshot editStart;
        bool editChanged;
        public int LengthMs { get; private set; }
        public bool Loop { get; private set; }
        public IReadOnlyList<CurveLaneModel> Lanes => lanes.AsReadOnly();
        public bool CanUndo => editStart == null && undo.Count > 0;
        public bool CanRedo => editStart == null && redo.Count > 0;
        public bool IsEditing => editStart != null;
        public event EventHandler Changed;
        public event EventHandler HistoryChanged;

        sealed class Snapshot
        {
            public int Length;
            public bool Loop;
            public Dictionary<string, CurveState> Curves;
            public Dictionary<string, string> Bindings;
        }

        public CurveDocument(int lengthMs = 6000, bool loop = true)
        { ValidateLength(lengthMs); LengthMs = lengthMs; Loop = loop; }

        public bool ContainsLane(string id) => id != null && byId.ContainsKey(id);
        public CurveLaneModel GetLane(string id)
        {
            if (id == null || !byId.TryGetValue(id, out var lane))
                throw new ArgumentException("Select an existing motor lane.", nameof(id));
            return lane;
        }

        public CurveLaneModel AddLane(string id, string displayName, IEnumerable<CurvePoint> points = null,
                                      CurveInterpolation interpolation = CurveInterpolation.Smooth)
        {
            if (string.IsNullOrWhiteSpace(id)) throw new ArgumentException("A lane needs a stable key.", nameof(id));
            if (ContainsLane(id)) throw new ArgumentException("This lane already exists.", nameof(id));
            if (string.IsNullOrWhiteSpace(displayName)) throw new ArgumentException("A lane needs a name.", nameof(displayName));
            EnsureNoEdit(); ValidateInterpolation(interpolation);
            var values = ValidatePoints(points ?? new[] { new CurvePoint(0, 0), new CurvePoint(LengthMs, 0) }, LengthMs);
            var lane = new CurveLaneModel(id, displayName, NewCurve(values, interpolation));
            lanes.Add(lane); byId.Add(id, lane); ClearHistory(); Notify();
            return lane;
        }

        public bool RemoveLane(string id)
        {
            EnsureNoEdit();
            if (id == null || !byId.TryGetValue(id, out var lane)) return false;
            lanes.Remove(lane); byId.Remove(id); ClearHistory(); Notify(); return true;
        }

        public void SetLaneAvailable(string id, bool available)
        {
            var lane = GetLane(id);
            if (lane.IsAvailable == available) return;
            lane.IsAvailable = available;
            // Connection state is not a document edit and must not interrupt a drag or
            // cause an output restart. Consumers may refresh their view directly.
        }

        public void RenameLane(string id, string displayName)
        {
            if (string.IsNullOrWhiteSpace(displayName)) throw new ArgumentException("A lane needs a name.", nameof(displayName));
            var lane = GetLane(id);
            if (lane.DisplayName == displayName) return;
            lane.DisplayName = displayName;
        }

        public CurvePoint[] ExportPoints(string id) => GetLane(id).Points.ToArray();
        public string[] LinkedLaneIds(string id)
        {
            var state = GetLane(id).State;
            return lanes.Where(lane => ReferenceEquals(lane.State, state)).Select(lane => lane.Id).ToArray();
        }

        public void ReplaceCurve(string id, IEnumerable<CurvePoint> points, CurveInterpolation? interpolation = null)
        {
            var lane = GetLane(id);
            var values = ValidatePoints(points, LengthMs);
            var mode = interpolation ?? lane.Interpolation; ValidateInterpolation(mode);
            if (lane.Points.SequenceEqual(values) && lane.Interpolation == mode) return;
            Apply(() => { lane.State.Points = Array.AsReadOnly(values); lane.State.Interpolation = mode; });
        }

        public int AddPoint(string id, int ms, int level)
        {
            var lane = GetLane(id);
            var point = new CurvePoint(ms, level);
            if (lane.Points.Count >= MaximumPoints) throw new ArgumentException("A curve can have at most 512 points.");
            var points = lane.Points.ToList();
            int index = points.FindIndex(p => p.Ms >= ms);
            if (index < 0) index = points.Count;
            points.Insert(index, point);
            ReplaceCurve(id, points); return index;
        }

        public void MovePoint(string id, int index, int ms, int level)
        {
            var lane = GetLane(id); CheckIndex(lane, index);
            var point = new CurvePoint(ms, level);
            if (lane.Points[index].Equals(point)) return;
            var points = ExportPoints(id); points[index] = point;
            ReplaceCurve(id, points);
        }

        public void DeletePoint(string id, int index)
        {
            var lane = GetLane(id); CheckIndex(lane, index);
            if (index == 0) throw new ArgumentException("The first point must remain at 0 ms.");
            if (lane.Points.Count <= 2) throw new ArgumentException("Keep at least two points.");
            var points = lane.Points.ToList(); points.RemoveAt(index); ReplaceCurve(id, points);
        }

        public void SetInterpolation(string id, CurveInterpolation interpolation)
        {
            ValidateInterpolation(interpolation); var lane = GetLane(id);
            if (lane.Interpolation == interpolation) return;
            Apply(() => lane.State.Interpolation = interpolation);
        }

        /// <summary>Replaces the named group's curves with a copy of the source, in one Undo.</summary>
        public void LinkCurves(string sourceId, IEnumerable<string> groupIds)
        {
            var source = GetLane(sourceId);
            if (groupIds == null) throw new ArgumentNullException(nameof(groupIds));
            var ids = groupIds.Distinct(StringComparer.Ordinal).ToArray();
            if (ids.Length < 2 || !ids.Contains(sourceId, StringComparer.Ordinal))
                throw new ArgumentException("Choose a source and at least one other motor.");
            var group = ids.Select(GetLane).ToArray();
            if (group.All(lane => ReferenceEquals(lane.State, source.State)) &&
                lanes.Count(lane => ReferenceEquals(lane.State, source.State)) == group.Length) return;
            var replacement = NewCurve(source.Points, source.Interpolation);
            Apply(() => { foreach (var lane in group) lane.State = replacement; });
        }

        public void Unlink(string id)
        {
            var lane = GetLane(id);
            if (LinkedLaneIds(id).Length < 2) return;
            var replacement = NewCurve(lane.Points, lane.Interpolation);
            Apply(() => lane.State = replacement);
        }

        /// <summary>Scale time offsets by default; rejects a length that would collide points.</summary>
        public void SetLength(int lengthMs, bool scalePoints = true)
        {
            ValidateLength(lengthMs); if (lengthMs == LengthMs) return;
            var replacements = new Dictionary<CurveState, CurvePoint[]>();
            foreach (var state in lanes.Select(lane => lane.State).Distinct())
            {
                var points = state.Points.Select(point => new CurvePoint(
                    scalePoints ? (int)Math.Round(point.Ms * (double)lengthMs / LengthMs, MidpointRounding.AwayFromZero) : point.Ms,
                    point.Level)).ToArray();
                replacements.Add(state, ValidatePoints(points, lengthMs));
            }
            Apply(() =>
            {
                foreach (var item in replacements) item.Key.Points = Array.AsReadOnly(item.Value);
                LengthMs = lengthMs;
            });
        }

        public void SetLoop(bool loop)
        { if (Loop != loop) Apply(() => Loop = loop); }

        public void BeginEdit()
        {
            if (editStart != null) throw new InvalidOperationException("An edit is already in progress.");
            editStart = Capture(); editChanged = false;
        }

        public void CommitEdit()
        {
            if (editStart == null) return;
            var previous = editStart; bool changed = editChanged;
            editStart = null; editChanged = false;
            if (changed && !Matches(previous)) { Push(undo, previous); redo.Clear(); }
            HistoryChanged?.Invoke(this, EventArgs.Empty);
            // No second Changed event: committing records history, not another mutation.
        }

        public void CancelEdit()
        {
            if (editStart == null) return;
            var previous = editStart; bool changed = editChanged;
            editStart = null; editChanged = false;
            if (changed && !Matches(previous)) { Restore(previous); Notify(); }
            HistoryChanged?.Invoke(this, EventArgs.Empty);
        }

        public bool Undo()
        {
            EnsureNoEdit(); if (undo.Count == 0) return false;
            var previous = Pop(undo); Push(redo, Capture()); Restore(previous); Notify(); HistoryChanged?.Invoke(this, EventArgs.Empty); return true;
        }

        public bool Redo()
        {
            EnsureNoEdit(); if (redo.Count == 0) return false;
            var next = Pop(redo); Push(undo, Capture()); Restore(next); Notify(); HistoryChanged?.Invoke(this, EventArgs.Empty); return true;
        }

        public void ClearHistory()
        {
            EnsureNoEdit(); undo.Clear(); redo.Clear(); HistoryChanged?.Invoke(this, EventArgs.Empty);
        }

        /// <summary>Clamped preview value; set useLoop only when sampling elapsed playback time.</summary>
        public double Sample(string id, double ms, bool useLoop = false)
        {
            if (!double.IsFinite(ms)) throw new ArgumentOutOfRangeException(nameof(ms));
            var lane = GetLane(id);
            double time = useLoop && Loop ? ((ms % LengthMs) + LengthMs) % LengthMs : Math.Clamp(ms, 0, LengthMs);
            var points = lane.Points;
            if (time >= points[points.Count - 1].Ms) return points[points.Count - 1].Level;
            int lo = 0, hi = points.Count - 1;
            while (hi - lo > 1) { int middle = (lo + hi) / 2; if (points[middle].Ms <= time) lo = middle; else hi = middle; }
            var a = points[lo]; var b = points[hi];
            return Interpolate(a.Level, b.Level, (time - a.Ms) / (b.Ms - a.Ms), lane.Interpolation);
        }

        public int LevelAt(string id, double ms, bool useLoop = false) =>
            Math.Clamp((int)Math.Round(Sample(id, ms, useLoop), MidpointRounding.AwayFromZero), 0, 20);

        public static double Interpolate(double start, double end, double fraction, CurveInterpolation interpolation)
        {
            ValidateInterpolation(interpolation);
            double t = Math.Clamp(fraction, 0, 1);
            if (interpolation == CurveInterpolation.Step) return t >= 1 ? end : start;
            if (interpolation == CurveInterpolation.Smooth) t = t * t * (3 - 2 * t);
            return start + (end - start) * t;
        }

        public static CurvePoint[] ValidatePoints(IEnumerable<CurvePoint> points, int lengthMs)
        {
            ValidateLength(lengthMs);
            if (points == null) throw new ArgumentNullException(nameof(points));
            // Bound enumeration as well as the final list, so accidental infinite input cannot hang.
            var values = points.Take(MaximumPoints + 1).ToArray();
            if (values.Length < 2 || values.Length > MaximumPoints || values[0].Ms != 0)
                throw new ArgumentException("Curves need 2–512 points, starting at 0 ms.");
            for (int i = 0; i < values.Length; i++)
            {
                if (values[i].Level < 0 || values[i].Level > 20 || values[i].Ms < 0 || values[i].Ms > lengthMs)
                    throw new ArgumentException("Points must be native levels 0–20 within the cycle.");
                if (i > 0 && values[i].Ms - values[i - 1].Ms < MinimumPointSpacingMs)
                    throw new ArgumentException("Points must be ordered and at least 100 ms apart.");
            }
            return values;
        }

        static void ValidateLength(int value)
        { if (value < MinimumLengthMs || value > MaximumLengthMs) throw new ArgumentOutOfRangeException(nameof(value), "Cycle length must be 200–600000 ms."); }
        static void ValidateInterpolation(CurveInterpolation mode)
        { if (!Enum.IsDefined(typeof(CurveInterpolation), mode)) throw new ArgumentOutOfRangeException(nameof(mode)); }
        static void CheckIndex(CurveLaneModel lane, int index)
        { if (index < 0 || index >= lane.Points.Count) throw new ArgumentOutOfRangeException(nameof(index)); }
        static CurveState NewCurve(IEnumerable<CurvePoint> points, CurveInterpolation mode) =>
            new CurveState(Guid.NewGuid().ToString("N"), points, mode);
        void EnsureNoEdit()
        { if (editStart != null) throw new InvalidOperationException("Finish the current point edit first."); }
        void Notify() => Changed?.Invoke(this, EventArgs.Empty);
        void Apply(Action mutation)
        {
            var previous = editStart == null ? Capture() : null;
            mutation();
            if (editStart == null) { Push(undo, previous); redo.Clear(); } else editChanged = true;
            Notify();
            HistoryChanged?.Invoke(this, EventArgs.Empty);
        }
        Snapshot Capture()
        {
            var state = new Snapshot { Length = LengthMs, Loop = Loop, Curves = new Dictionary<string, CurveState>(),
                Bindings = new Dictionary<string, string>(StringComparer.Ordinal) };
            foreach (var lane in lanes)
            {
                state.Bindings.Add(lane.Id, lane.State.Id);
                if (!state.Curves.ContainsKey(lane.State.Id)) state.Curves.Add(lane.State.Id, lane.State.Copy());
            }
            return state;
        }
        bool Matches(Snapshot state)
        {
            if (LengthMs != state.Length || Loop != state.Loop || lanes.Count != state.Bindings.Count) return false;
            foreach (var lane in lanes)
            {
                if (!state.Bindings.TryGetValue(lane.Id, out var group) || group != lane.GroupId) return false;
                var original = state.Curves[group];
                if (lane.Interpolation != original.Interpolation || !lane.Points.SequenceEqual(original.Points)) return false;
            }
            return true;
        }
        void Restore(Snapshot state)
        {
            var copies = state.Curves.ToDictionary(pair => pair.Key, pair => pair.Value.Copy());
            foreach (var lane in lanes) lane.State = copies[state.Bindings[lane.Id]];
            LengthMs = state.Length; Loop = state.Loop;
        }
        static void Push(List<Snapshot> stack, Snapshot state)
        { stack.Add(state); if (stack.Count > HistoryLimit) stack.RemoveAt(0); }
        static Snapshot Pop(List<Snapshot> stack)
        { var result = stack[stack.Count - 1]; stack.RemoveAt(stack.Count - 1); return result; }
    }
}


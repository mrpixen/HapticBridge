using System;
using System.Linq;

namespace LovenseConnector
{
    internal static class CurveDocumentTests
    {
        static int passed;
        static void Check(bool ok, string name)
        { if (!ok) throw new Exception(name); passed++; Console.WriteLine("PASS " + name); }
        static void Reject(Action action, string name)
        {
            bool rejected = false;
            try { action(); } catch (ArgumentException) { rejected = true; }
            Check(rejected, name);
        }
        static void RejectOperation(Action action, string name)
        {
            bool rejected = false;
            try { action(); } catch (InvalidOperationException) { rejected = true; }
            Check(rejected, name);
        }
        static CurvePoint[] Points(params int[] pairs)
        { return Enumerable.Range(0, pairs.Length / 2).Select(i => new CurvePoint(pairs[i * 2], pairs[i * 2 + 1])).ToArray(); }
        static CurveDocument Make()
        {
            var doc = new CurveDocument(1000);
            doc.AddLane("motor-a", "Motor A", Points(0, 0, 500, 10, 1000, 0));
            doc.AddLane("motor-b", "Motor B", Points(0, 4, 500, 4, 1000, 4));
            doc.AddLane("motor-c", "Motor C", Points(0, 2, 1000, 2));
            return doc;
        }

        public static int Main()
        {
            try
            {
                passed = 0;
                Reject(() => new CurvePoint(0, 21), "curve rejects levels above native range");
                Reject(() => new CurvePoint(-1, 0), "curve rejects negative times");
                Reject(() => new CurveDocument(199), "curve rejects too-short cycles");
                Reject(() => new CurveDocument(600001), "curve rejects oversized cycles");
                var doc = Make();
                Check(!doc.CanUndo && doc.Lanes.Count == 3, "lane discovery establishes topology without an undo edit");
                Reject(() => doc.AddLane("motor-a", "Duplicate"), "duplicate lane keys do not overwrite an existing curve");
                Reject(() => doc.ReplaceCurve("motor-a", Points(1, 0, 500, 5)), "first point must remain at zero");
                Reject(() => doc.ReplaceCurve("motor-a", Points(0, 0, 99, 1)), "minimum spacing matches playback engine");
                Reject(() => doc.ReplaceCurve("motor-a", Points(0, 0, 700, 1, 600, 2)), "unsorted points are rejected");
                Reject(() => doc.ReplaceCurve("motor-a", Points(0, 0, 1001, 1)), "points outside the cycle are rejected");
                Reject(() => doc.ReplaceCurve("motor-a", Points(0, 0)), "at least two points are required");
                Reject(() => doc.SetInterpolation("motor-a", (CurveInterpolation)77), "unknown interpolation is rejected");
                Check(doc.GetLane("motor-a").Points.SequenceEqual(Points(0, 0, 500, 10, 1000, 0)) && !doc.CanUndo,
                    "invalid edits preserve both data and undo history");
                Reject(() => doc.MovePoint("motor-a", 0, 100, 7), "time of the first point cannot move");
                Reject(() => doc.DeletePoint("motor-a", 0), "first point cannot be deleted");
                Reject(() => doc.DeletePoint("motor-c", 1), "two-point minimum is enforced on deletion");

                var originalB = doc.ExportPoints("motor-b");
                doc.LinkCurves("motor-a", new[] { "motor-a", "motor-b" });
                Check(doc.GetLane("motor-a").GroupId == doc.GetLane("motor-b").GroupId &&
                      doc.GetLane("motor-b").Points.SequenceEqual(doc.GetLane("motor-a").Points),
                    "link explicitly replaces the destination with the source curve");
                doc.MovePoint("motor-b", 1, 600, 16);
                Check(doc.GetLane("motor-a").Points[1].Equals(new CurvePoint(600, 16)) &&
                      doc.GetLane("motor-c").Points[1].Level == 2,
                    "editing either group member changes the shared curve only");
                doc.Undo();
                Check(doc.GetLane("motor-a").Points[1].Equals(new CurvePoint(500, 10)) &&
                      doc.GetLane("motor-a").GroupId == doc.GetLane("motor-b").GroupId,
                    "undo a group edit preserves group identity");
                doc.Undo();
                Check(doc.GetLane("motor-b").Points.SequenceEqual(originalB) &&
                      doc.GetLane("motor-a").GroupId != doc.GetLane("motor-b").GroupId,
                    "one undo restores all pre-link destination values and membership");
                doc.Redo();
                Check(doc.GetLane("motor-a").GroupId == doc.GetLane("motor-b").GroupId,
                    "redo restores a linked group");
                doc.Unlink("motor-b");
                var unlinkedB = doc.ExportPoints("motor-b");
                doc.MovePoint("motor-a", 1, 500, 19);
                Check(doc.GetLane("motor-b").Points.SequenceEqual(unlinkedB),
                    "unlink retains the current curve as an independent copy");
                doc.Undo(); doc.Undo();
                Check(doc.GetLane("motor-a").GroupId == doc.GetLane("motor-b").GroupId,
                    "unlink itself is atomic and undoable");
                doc.LinkCurves("motor-a", new[] { "motor-a", "motor-c" });
                doc.MovePoint("motor-c", 1, 500, 7);
                Check(doc.GetLane("motor-a").Points[1].Level == 7 && doc.GetLane("motor-b").Points[1].Level == 10,
                    "relinking a subset leaves excluded former group members intact");
                var beforeInvalidLink = doc.ExportPoints("motor-a");
                Reject(() => doc.LinkCurves("motor-a", new[] { "motor-a", "missing" }),
                    "unknown group member is rejected before any replacement");
                Check(doc.GetLane("motor-a").Points.SequenceEqual(beforeInvalidLink),
                    "failed link leaves all source values unchanged");
                Reject(() => doc.LinkCurves("motor-a", new[] { "motor-b", "motor-c" }),
                    "a linked group must include its explicit source");

                doc = Make();
                int changes = 0, historyChanges = 0;
                doc.Changed += (sender, args) => changes++;
                doc.HistoryChanged += (sender, args) => historyChanges++;
                doc.BeginEdit();
                doc.MovePoint("motor-a", 1, 500, 11);
                doc.MovePoint("motor-a", 1, 600, 12);
                doc.MovePoint("motor-a", 1, 700, 13);
                Check(!doc.CanUndo && changes == 3, "drag previews emit mutations while deferring undo");
                doc.CommitEdit();
                Check(doc.CanUndo && changes == 3 && historyChanges > 0, "drag commit records history without a duplicate mutation");
                doc.Undo();
                Check(doc.GetLane("motor-a").Points[1].Equals(new CurvePoint(500, 10)) && !doc.CanUndo,
                    "one undo reverses the whole drag");
                doc.BeginEdit(); doc.MovePoint("motor-a", 1, 500, 2); doc.CancelEdit();
                Check(doc.GetLane("motor-a").Points[1].Level == 10 && doc.CanRedo,
                    "cancelled drag restores the prior document and existing redo");
                doc.Redo();
                Check(doc.GetLane("motor-a").Points[1].Equals(new CurvePoint(700, 13)),
                    "redo survives a cancelled drag");
                doc.ClearHistory();
                doc.BeginEdit(); doc.MovePoint("motor-a", 1, 700, 12); doc.MovePoint("motor-a", 1, 700, 13); doc.CommitEdit();
                Check(!doc.CanUndo, "dragging back to the original point creates no empty undo item");
                doc.BeginEdit();
                RejectOperation(() => doc.AddLane("motor-d", "Motor D"), "topology cannot change during a point transaction");
                RejectOperation(() => doc.Undo(), "undo cannot partially restore an open edit");
                doc.CancelEdit();

                doc = Make(); doc.LinkCurves("motor-a", new[] { "motor-a", "motor-b" }); doc.ClearHistory();
                doc.SetLength(2000);
                Check(doc.GetLane("motor-a").Points[1].Ms == 1000 && doc.GetLane("motor-b").Points[2].Ms == 2000 &&
                      doc.GetLane("motor-a").GroupId == doc.GetLane("motor-b").GroupId,
                    "cycle scaling transforms each shared curve once");
                doc.Undo();
                Check(doc.LengthMs == 1000 && doc.GetLane("motor-b").Points[1].Ms == 500,
                    "length and all affected points undo together");
                doc.ReplaceCurve("motor-a", Points(0, 0, 100, 5, 1000, 0)); doc.ClearHistory();
                Reject(() => doc.SetLength(200), "shrinking cannot silently collide or discard points");
                Check(doc.LengthMs == 1000 && !doc.CanUndo, "failed resize is atomic");
                Reject(() => doc.SetLength(500, false), "unscaled resize rejects points beyond its end");

                doc = new CurveDocument(1000, true);
                doc.AddLane("motor-a", "Motor A", Points(0, 0, 1000, 20), CurveInterpolation.Smooth);
                Check(doc.LevelAt("motor-a", 250) == 3 && Math.Abs(doc.Sample("motor-a", 250) - 3.125) < .0001,
                    "smooth curve uses bounded smoothstep with native integer rounding");
                Check(Enumerable.Range(0, 1001).All(ms => doc.Sample("motor-a", ms) >= 0 && doc.Sample("motor-a", ms) <= 20),
                    "smooth interpolation never overshoots the native range");
                doc.SetInterpolation("motor-a", CurveInterpolation.Linear);
                Check(doc.LevelAt("motor-a", 250) == 5, "linear curve interpolates directly");
                doc.SetInterpolation("motor-a", CurveInterpolation.Step);
                Check(doc.LevelAt("motor-a", 999) == 0 && doc.LevelAt("motor-a", 1000) == 20,
                    "step curve changes only at an exact point boundary");
                Check(doc.LevelAt("motor-a", 1000, true) == 0, "explicit elapsed-time sampling wraps at loop boundary");
                doc.SetLoop(false);
                Check(doc.LevelAt("motor-a", 2000, true) == 20, "one-shot sampling holds the final endpoint");
                Reject(() => doc.Sample("motor-a", double.NaN), "nonfinite preview time is rejected");
                var exported = doc.ExportPoints("motor-a"); exported[0] = new CurvePoint(0, 17);
                Check(doc.GetLane("motor-a").Points[0].Level == 0, "export returns a copy and cannot mutate the live document");
                int metadataChanges = 0; doc.Changed += (sender, args) => metadataChanges++;
                doc.SetLaneAvailable("motor-a", false); doc.RenameLane("motor-a", "Renamed motor");
                Check(metadataChanges == 0 && !doc.GetLane("motor-a").IsAvailable,
                    "connection and naming updates do not masquerade as pattern edits");

                doc = new CurveDocument(600000);
                var maximum = Enumerable.Range(0, 512).Select(i => new CurvePoint(i * 100, i % 21)).ToArray();
                doc.AddLane("motor-a", "Motor A", maximum);
                Reject(() => doc.AddPoint("motor-a", 51200, 3), "point-count limit is enforced before insertion");
                Reject(() => doc.ReplaceCurve("motor-a", Enumerable.Range(0, 513).Select(i => new CurvePoint(i * 100, 0))),
                    "import rejects more than 512 points");
                Check(doc.GetLane("motor-a").Points.Count == 512, "oversized edits leave a valid maximum-size curve intact");

                Console.WriteLine(passed + " curve document checks passed.");
                return 0;
            }
            catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
        }
    }
}


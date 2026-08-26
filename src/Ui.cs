using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;
using PavonisInteractive.TerraInvicta;

namespace TerraInvictaMCP
{
    // ui.screenshot, ui.tooltip.
    //
    // Two ways to read the screen without the screen. The capture runs inside the
    // process, so it sees the game's own back buffer rather than whatever the
    // desktop has on top of the window; the tooltip verb calls the same static
    // builders the hover text is made of, so the string is the player's, not a
    // reconstruction of it.
    public static partial class Verbs
    {
        #region ui.screenshot

        // Under persistentDataPath, which is the one directory the game process
        // and the host agree on: the server maps it as the folder Player.log
        // sits in.
        const string ShotFolder = "mcp-screenshots";

        // Captures kept on disk. The server deletes each file after reading it,
        // so this only bounds what a raw caller leaves behind.
        const int MaxShotsKept = 20;

        // Unity accepts 1-4, but 4 is 16x the pixels: the PNG runs to tens of
        // megabytes, takes long enough that a client's wait budget has to grow
        // with it, and is far past what fits in a tool response as an image. Two
        // is the useful end of the range (small UI text becomes legible), so the
        // verb stops there and the wait stays predictable.
        const int MaxSupersize = 2;

        static string lastShotName;
        static string lastShotPath;
        static string lastShotRelative;
        static int lastShotSupersize;
        static int lastShotFrame = -1;
        static int shotCounter;

        static JToken UiScreenshot(JObject args)
        {
            if (Flag(args, "status")) return ShotStatus(false);

            // Read through the token rather than OptionalInt's -1-means-absent:
            // a supersize is a small positive number, so a negative one is a
            // mistake that must not quietly become the default.
            int supersize = 1;
            JToken sizeArg = args != null ? args["supersize"] : null;
            if (sizeArg != null && sizeArg.Type != JTokenType.Null)
            {
                supersize = Int(args, "supersize");
                if (supersize < 1 || supersize > MaxSupersize)
                    throw new VerbError("arg 'supersize' must be 1.." + MaxSupersize);
            }

            string dir = ShotDir();
            string name = ShotName(args);
            string path = Path.Combine(dir, name);

            try { Directory.CreateDirectory(dir); }
            catch (Exception e)
            {
                throw new VerbError("could not create " + dir + ": " + Note(e));
            }
            // A stale file at this path would make `exists` mean "some capture
            // once landed here" instead of "this one did", and the file is the
            // only completion signal there is.
            try { if (File.Exists(path)) File.Delete(path); }
            catch (Exception e)
            {
                throw new VerbError("a file is already at " + path
                    + " and could not be removed: " + Note(e));
            }
            Prune(dir);

            // Unity finishes the capture at the end of the current frame and
            // writes the PNG from native code, so nothing here can wait for it:
            // the verb arms the capture and the file's appearance is the
            // completion signal. CaptureScreenshot(path) is CaptureScreenshot with
            // superSize 1, so the two calls differ only in resolution.
            if (supersize > 1)
                UnityEngine.ScreenCapture.CaptureScreenshot(path, supersize);
            else
                UnityEngine.ScreenCapture.CaptureScreenshot(path);

            lastShotName = name;
            lastShotPath = path;
            lastShotRelative = ShotFolder + "/" + name;
            lastShotSupersize = supersize;
            lastShotFrame = Safe<int>(
                delegate { return UnityEngine.Time.frameCount; }, -1);
            return ShotStatus(true);
        }

        static JObject ShotStatus(bool armedNow)
        {
            var o = new JObject();
            o["armed"] = lastShotPath != null;
            o["armedNow"] = armedNow;
            if (lastShotPath == null)
            {
                o["name"] = JValue.CreateNull();
                o["path"] = JValue.CreateNull();
                o["relativePath"] = JValue.CreateNull();
                o["dir"] = PersistentRoot();
                o["folder"] = ShotDir();
                o["exists"] = false;
                o["bytes"] = -1;
                o["frames"] = -1;
                o["supersize"] = 0;
                return o;
            }
            o["name"] = lastShotName;
            // The path as the process sees it (a Windows path under Proton), for
            // a human. `relativePath` is relative to `dir`, which is
            // persistentDataPath: a client outside the process joins the two,
            // using its own view of that directory (the one Player.log sits in).
            o["path"] = lastShotPath;
            o["relativePath"] = lastShotRelative;
            o["dir"] = PersistentRoot();
            o["folder"] = ShotDir();
            o["supersize"] = lastShotSupersize;
            long bytes = Safe<long>(delegate
            {
                var info = new FileInfo(lastShotPath);
                return info.Exists ? info.Length : -1L;
            }, -1L);
            // No "pending" flag: a file that exists may still be growing, and any
            // single read of its size -- zero or not -- can land mid-write, so a
            // flag derived from one read would report done before it is. `bytes`
            // is the raw signal and the caller decides: the capture is finished
            // when two reads a moment apart return the same positive size.
            o["exists"] = bytes >= 0;
            o["bytes"] = bytes;
            o["frames"] = lastShotFrame < 0 ? -1
                : Safe<int>(delegate
                    { return UnityEngine.Time.frameCount - lastShotFrame; }, -1);
            return o;
        }

        // persistentDataPath itself, which is what relativePath is relative to.
        // A client outside the game process cannot see the process's own path
        // (it is a Windows path inside the Wine prefix on Linux), but it can
        // find this directory: it is the one Player.log sits in.
        static string PersistentRoot()
        {
            string root = Safe<string>(
                delegate { return UnityEngine.Application.persistentDataPath; }, null);
            if (string.IsNullOrEmpty(root))
                throw new VerbError("no persistentDataPath");
            return root;
        }

        static string ShotDir()
        {
            return Path.Combine(PersistentRoot(), ShotFolder);
        }

        // A plain file name in the shot folder, PNG whatever was asked for:
        // ScreenCapture writes PNG regardless of the extension, and a .jpg that
        // holds PNG bytes is a trap for whatever reads it.
        static string ShotName(JObject args)
        {
            string name = Str(args, "name");
            if (string.IsNullOrEmpty(name))
            {
                shotCounter++;
                return string.Format(CultureInfo.InvariantCulture,
                    "mcp-{0:yyyyMMdd-HHmmss}-{1:D3}.png", DateTime.Now, shotCounter);
            }
            name = name.Trim();
            if (name.Length == 0) throw new VerbError("arg 'name' is empty");
            if (name.IndexOf('/') >= 0 || name.IndexOf('\\') >= 0
                || name.IndexOf(':') >= 0 || name.IndexOf("..") >= 0)
                throw new VerbError("arg 'name' must be a plain file name");
            int dot = name.IndexOf('.');
            string stem = dot >= 0 ? name.Substring(0, dot) : name;
            for (int i = 0; i < ReservedNames.Length; i++)
            {
                if (string.Equals(stem, ReservedNames[i], StringComparison.OrdinalIgnoreCase))
                    throw new VerbError("arg 'name' is a reserved device name");
            }
            if (!name.EndsWith(".png", StringComparison.OrdinalIgnoreCase))
                name = stem + ".png";
            return name;
        }

        // Best effort, and silent: a capture that lands is worth more than a
        // tidy folder, so nothing here may fail the verb.
        static void Prune(string dir)
        {
            try
            {
                string[] files = Directory.GetFiles(dir, "*.png");
                if (files.Length <= MaxShotsKept) return;
                Array.Sort(files, delegate(string a, string b)
                {
                    return File.GetLastWriteTimeUtc(a).CompareTo(
                        File.GetLastWriteTimeUtc(b));
                });
                for (int i = 0; i < files.Length - MaxShotsKept; i++)
                {
                    try { File.Delete(files[i]); }
                    catch (Exception) { }
                }
            }
            catch (Exception) { }
        }

        #endregion

        #region ui.tooltip

        // NationInfoController's tooltip builders are public statics taking one
        // nation and returning the built string, with no controller instance and
        // no active-player read in any of them, so they answer with the screen
        // never opened. Bound at compile time rather than by reflection: a game
        // update that renames one breaks the build instead of the verb.
        //
        // BuildRegionDataTooltip is deliberately absent -- it takes a region, a
        // faction and a viewing nation, which is a different verb's shape.
        static readonly Dictionary<string, Func<TINationState, string>> tooltips
            = BuildTooltipTable();

        static Dictionary<string, Func<TINationState, string>> BuildTooltipTable()
        {
            var t = new Dictionary<string, Func<TINationState, string>>(
                StringComparer.Ordinal);
            t["investment"] = delegate(TINationState n)
                { return NationInfoController.BuildInvestmentTooltip(n); };
            t["publicopinion"] = delegate(TINationState n)
                { return NationInfoController.BuildPublicOpinionTooltip(n); };
            t["specialrelationship"] = delegate(TINationState n)
                { return NationInfoController.BuildSpecialRelationshipTooltip(n); };
            t["nukes"] = delegate(TINationState n)
                { return NationInfoController.BuildNukesTooltip(n); };
            t["armies"] = delegate(TINationState n)
                { return NationInfoController.BuildnumArmiesTooltip(n); };
            t["stofighters"] = delegate(TINationState n)
                { return NationInfoController.BuildSTOFightersTooltip(n); };
            t["education"] = delegate(TINationState n)
                { return NationInfoController.BuildEducationTooltip(n); };
            t["cohesion"] = delegate(TINationState n)
                { return NationInfoController.BuildCohesionTooltip(n); };
            t["inequality"] = delegate(TINationState n)
                { return NationInfoController.BuildInequalityTooltip(n); };
            t["unrest"] = delegate(TINationState n)
                { return NationInfoController.BuildUnrestTooltip(n); };
            t["democracy"] = delegate(TINationState n)
                { return NationInfoController.BuildDemocracyTooltip(n); };
            t["population"] = delegate(TINationState n)
                { return NationInfoController.BuildPopulationTooltip(n); };
            t["miltech"] = delegate(TINationState n)
                { return NationInfoController.BuildMiltechTooltip(n); };
            t["percapitagdp"] = delegate(TINationState n)
                { return NationInfoController.BuildPerCapitaGDPTooltip(n); };
            t["gdp"] = delegate(TINationState n)
                { return NationInfoController.BuildGDPTooltip(n); };
            t["sustainability"] = delegate(TINationState n)
                { return NationInfoController.BuildSustainabilityTooltip(n); };
            t["spacefunding"] = delegate(TINationState n)
                { return NationInfoController.BuildSpaceFundingTooltip(n); };
            t["research"] = delegate(TINationState n)
                { return NationInfoController.BuildResearchTooltip(n); };
            t["boost"] = delegate(TINationState n)
                { return NationInfoController.BuildBoostTooltip(n); };
            t["missioncontrol"] = delegate(TINationState n)
                { return NationInfoController.BuildMissionControlTooltip(n); };
            t["naval"] = delegate(TINationState n)
                { return NationInfoController.BuildNavalTooltip(n); };
            // Its second argument defaults to true, which is the panel's own
            // call and the string a player reads.
            t["policies"] = delegate(TINationState n)
                { return NationInfoController.BuildPoliciesTooltip(n); };
            return t;
        }

        // Rich text the builders emit for the UI: TMP colour and sprite tags.
        static readonly Regex tagPattern = new Regex("<[^<>]*>", RegexOptions.None);

        static JToken UiTooltip(JObject args)
        {
            TINationState nation = ArgNation(args, "nation");
            string kind = Str(args, "kind");
            if (string.IsNullOrEmpty(kind))
                throw new VerbError("missing arg 'kind'; " + TooltipKinds());
            string key = TooltipKey(kind);
            Func<TINationState, string> build;
            if (!tooltips.TryGetValue(key, out build))
                throw new VerbError("unknown tooltip kind '" + kind + "'; "
                    + TooltipKinds());

            // The one builder with a gate of its own: it enumerates the nation's
            // set-policy options, which reach the executive control point through
            // a bare indexer and throw for a nation that holds none.
            if (string.Equals(key, "policies", StringComparison.Ordinal))
                RequireExecutiveControlPoint(nation);

            string text;
            try { text = build(nation); }
            catch (Exception e)
            {
                throw new VerbError("the " + key + " tooltip builder threw for "
                    + StateName(nation) + " (" + (int)nation.ID + "): " + Note(e));
            }
            if (text == null) text = "";

            var o = new JObject();
            o["nation"] = Describe(nation);
            o["kind"] = key;
            o["text"] = text;
            // The same string with the TMP markup removed, for an assertion that
            // should not care whether a number came out coloured.
            o["plain"] = Safe<JToken>(
                delegate { return (JToken)tagPattern.Replace(text, ""); },
                new JValue(text));
            o["length"] = text.Length;
            return o;
        }

        // Case, spaces, dashes, dots and underscores all fall away, so
        // "publicOpinion", "public_opinion" and "Public Opinion" are one kind.
        static string TooltipKey(string kind)
        {
            var sb = new System.Text.StringBuilder(kind.Length);
            for (int i = 0; i < kind.Length; i++)
            {
                char c = kind[i];
                if (char.IsLetterOrDigit(c)) sb.Append(char.ToLowerInvariant(c));
            }
            return sb.ToString();
        }

        static string TooltipKinds()
        {
            var names = new List<string>(tooltips.Keys);
            names.Sort(StringComparer.Ordinal);
            return "kind is one of: " + string.Join(", ", names.ToArray());
        }

        #endregion
    }
}

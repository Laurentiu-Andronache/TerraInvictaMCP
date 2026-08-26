using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEngine;
using PavonisInteractive.TerraInvicta;
using PavonisInteractive.TerraInvicta.Debugging;
using PavonisInteractive.TerraInvicta.Systems.Bootstrap;
using PavonisInteractive.TerraInvicta.Systems.GameTime;

namespace TerraInvictaMCP
{
    // Thrown for expected failures; the message goes straight into the error frame.
    public class VerbError : Exception
    {
        public VerbError(string message) : base(message) { }
    }

    delegate JToken VerbFn(JObject args);

    class Verb
    {
        public VerbFn fn;
        public bool needsCampaign;
    }

    public static partial class Verbs
    {
        public const string ModVersion = "0.1.0";

        // query.state fans out over one object's members; a ref list longer than this
        // is a graph the client should walk with more queries instead.
        const int MaxRefListLength = 200;

        static readonly Dictionary<string, Verb> table = BuildTable();

        static Dictionary<string, Verb> BuildTable()
        {
            var t = new Dictionary<string, Verb>(StringComparer.Ordinal);
            Add(t, "ping", false, Ping);
            Add(t, "version", false, Version);
            Add(t, "verbs", false, VerbsList);
            Add(t, "console", true, Console);
            Add(t, "select", true, Select);
            Add(t, "query.time", true, QueryTime);
            Add(t, "query.factions", true, QueryFactions);
            Add(t, "query.habs", true, QueryHabs);
            Add(t, "query.fleets", true, QueryFleets);
            Add(t, "query.state", true, QueryState);
            Add(t, "time.pause", true, TimePause);
            Add(t, "time.play", true, TimePlay);
            Add(t, "time.speed", true, TimeSpeed);
            Add(t, "time.run_until", true, TimeRunUntil);
            Add(t, "saves.list", false, SavesList);
            Add(t, "saves.save", true, SavesSave);
            // Loading works from the main menu; it is the hands-off entry point.
            Add(t, "saves.load", false, SavesLoad);
            // A new campaign is started from the main menu, so there is no campaign yet.
            Add(t, "campaign.new", false, CampaignNew);
            Add(t, "prompts.list", true, PromptsList);
            Add(t, "prompts.dismiss", true, PromptsDismiss);
            Add(t, "alert.choose", true, AlertChoose);
            Add(t, "spawn.fleet", true, SpawnFleet);
            Add(t, "combat.start", true, CombatStart);
            Add(t, "combat.status", true, CombatStatus);
            Add(t, "combat.autoresolve", true, CombatAutoresolve);
            // Orbital bombardment: the one fleet order with no console command and no
            // reachable UI path for a fleet the player did not build.
            Add(t, "fleet.bombard", true, FleetBombard);
            // National policy: no console command sets one, and the UI path runs through
            // a councilor mission. These take the engine's own AI enactment instead, with
            // its legality intact.
            Add(t, "nation.policies", true, NationPolicies);
            Add(t, "nation.set_policy", true, NationSetPolicy);
            // The five HandledAtFactionLevel options, which are exactly what
            // nation.set_policy refuses. The only other way to them is
            // action.invoke ConfirmPolicyAction, which is the raw bypass: no cost,
            // no eligibility, no confirm. This is the contracted path.
            Add(t, "faction.diplomacy", true, FactionDiplomacy);
            // Nation stats the console cannot reach, forced directly. Unlike the
            // two policy verbs above this is a fixture: it bypasses every rule
            // that would normally move the number.
            Add(t, "nation.set_stat", true, NationSetStat);
            // Hab modules, the one destructible the console's killstate does not
            // cover: DestroyModule needs the owning faction's Habs screen.
            Add(t, "kill.module", true, KillModule);
            // The Habitats screen's power toggle, headless: the same
            // SetPowerStatus call its action makes, gated on the screen's own
            // preconditions because the engine's coercion is silent.
            Add(t, "module.power", true, ModuleSetPower);
            // Mod inspection is campaign-free: templates and both mod loaders finish at
            // boot, long before any campaign exists.
            Add(t, "mods.list", false, ModsList);
            Add(t, "query.template", false, QueryTemplate);
            // Pure reflection over the loaded assembly, so it answers at the main menu.
            Add(t, "query.enums", false, QueryEnums);
            Add(t, "harmony.patches", false, HarmonyPatches);
            // Testing verbs. Localization, the scenario picker, the duplicate
            // tables and the bundle registries are all filled at boot by GlobalInstaller,
            // so those answer with no campaign loaded; the loaded scenario, designs,
            // councilors and nations are campaign state.
            Add(t, "query.localize", false, QueryLocalize);
            Add(t, "query.scenarios", false, QueryScenarios);
            Add(t, "query.scenario", true, QueryScenario);
            Add(t, "query.templateDupes", false, QueryTemplateDupes);
            Add(t, "assets.bundles", false, AssetsBundles);
            Add(t, "assets.resolve", false, AssetsResolve);
            Add(t, "query.designs", true, QueryDesigns);
            Add(t, "query.councilors", true, QueryCouncilors);
            Add(t, "query.nations", true, QueryNations);
            // Fixture verbs. All of them write into campaign state through the
            // game's own construction paths, so all of them need one loaded.
            Add(t, "spawn.hab", true, SpawnHab);
            Add(t, "spawn.module", true, SpawnModule);
            Add(t, "spawn.army", true, SpawnArmy);
            Add(t, "spawn.councilor", true, SpawnCouncilor);
            Add(t, "spawn.alien_site", true, SpawnAlienSite);
            // True autopilot: hands the player faction to the real faction AI.
            Add(t, "ai.control", true, AiControl_Verb);
            // Reading the screen without the screen. The capture answers at the
            // main menu too, which is where a launch is verified; the tooltip
            // builders read a nation, so they need a campaign.
            Add(t, "ui.screenshot", false, UiScreenshot);
            Add(t, "ui.tooltip", true, UiTooltip);
            // The whole player-action catalog, reflectively. The listing is pure
            // reflection over the loaded assembly, so it answers at the main menu;
            // invoking one writes campaign state.
            Add(t, "action.list", false, ActionList);
            Add(t, "action.invoke", true, ActionInvoke);
            return t;
        }

        static void Add(Dictionary<string, Verb> t, string name, bool needsCampaign, VerbFn fn)
        {
            var v = new Verb();
            v.fn = fn;
            v.needsCampaign = needsCampaign;
            t[name] = v;
        }

        public static string Execute(Request req)
        {
            long id = req != null ? req.id : 0;
            try
            {
                if (req == null) return Error(0, "empty request");
                if (req.parseError != null) return Error(id, req.parseError);
                Verb verb;
                if (!table.TryGetValue(req.cmd, out verb))
                    return Error(id, "unknown command '" + req.cmd + "'");
                if (verb.needsCampaign && !HasCampaign) return Error(id, "no campaign");
                JToken data = verb.fn(req.args != null ? req.args : new JObject());
                return Ok(id, data);
            }
            catch (VerbError e)
            {
                return Error(id, e.Message);
            }
            catch (Exception e)
            {
                return Error(id, Note(e));
            }
        }

        // Global values exist early in the load, minutes before visualizers finish;
        // mutating verbs in that window corrupt the campaign (observed: fleets spawned
        // mid-bootstrap crashed the visualizer loader). loadcycle100 is the game's own
        // "100% Loaded" flag, cleared again on unload.
        public static bool HasCampaign
        {
            get
            {
                try { return GameStateManager.GlobalValues() != null && GameControl.loadcycle100; }
                catch (Exception) { return false; }
            }
        }

        static string Ok(long id, JToken data)
        {
            var o = new JObject();
            o["id"] = id;
            o["ok"] = true;
            o["data"] = data != null ? data : JValue.CreateNull();
            return o.ToString(Formatting.None);
        }

        internal static string Error(long id, string message)
        {
            var o = new JObject();
            o["id"] = id;
            o["ok"] = false;
            o["error"] = string.IsNullOrEmpty(message) ? "error" : message;
            return o.ToString(Formatting.None);
        }

        static JToken Ping(JObject args)
        {
            return new JValue("pong");
        }

        static JToken Version(JObject args)
        {
            var o = new JObject();
            o["mod"] = ModVersion;
            o["game"] = Application.version;
            return o;
        }

        // The dispatch table itself, so a client can detect verb drift against the
        // build it was written for.
        static JToken VerbsList(JObject args)
        {
            var names = new List<string>(table.Keys);
            names.Sort(StringComparer.Ordinal);
            var a = new JArray();
            for (int i = 0; i < names.Count; i++)
            {
                var o = new JObject();
                o["name"] = names[i];
                o["needsCampaign"] = table[names[i]].needsCampaign;
                a.Add(o);
            }
            var result = new JObject();
            result["verbs"] = a;
            return result;
        }

        static JToken Console(JObject args)
        {
            string line = Str(args, "line");
            if (string.IsNullOrEmpty(line)) throw new VerbError("missing arg 'line'");

            var container = GlobalInstaller.container;
            if (container == null) throw new VerbError("no DI container");
            TerminalController terminal = container.Resolve<TerminalController>();
            if (terminal == null) throw new VerbError("no terminal controller");

            var output = new List<string>();
            var errors = new List<string>();
            Action<string> onOutput = delegate(string s) { output.Add(s); };
            Action<string> onError = delegate(string s) { errors.Add(s); };

            terminal.OnOutput += onOutput;
            terminal.OnOutputError += onError;
            try
            {
                terminal.ParseCommand(line);
            }
            finally
            {
                terminal.OnOutput -= onOutput;
                terminal.OnOutputError -= onError;
            }

            var o = new JObject();
            o["output"] = ToArray(output);
            o["errors"] = ToArray(errors);
            return o;
        }

        static JToken Select(JObject args)
        {
            TIGameState state = Arg<TIGameState>(args, "id");
            GeneralControlsController.SetUIOtherSelectedState(state);
            return Describe(state);
        }

        static JToken QueryTime(JObject args)
        {
            GameTimeManager manager = GameTimeManager.Singleton;
            if (manager == null) throw new VerbError("no campaign");
            TIDateTime now = manager.currentTime;
            var o = new JObject();
            o["date"] = now != null ? InvariantDate(now) : null;
            o["speed"] = manager.currentSpeedIndex;
            o["paused"] = manager.Paused;
            // Paused is just speed index 0. A blocking prompt freezes time with the
            // index untouched, so a client polling for progress needs both.
            o["blocked"] = Blocked(manager);
            o["armed"] = runUntilTarget != null;
            o["run_until"] = runUntilText;
            return o;
        }

        static JToken Blocked(GameTimeManager manager)
        {
            return Safe<JToken>(delegate { return new JValue(manager.IsBlocked); }, JValue.CreateNull());
        }

        static JToken QueryFactions(JObject args)
        {
            var a = new JArray();
            TIFactionState[] factions = GameStateManager.AllFactions();
            if (factions == null) return a;
            for (int i = 0; i < factions.Length; i++)
            {
                TIFactionState f = factions[i];
                if (f == null) continue;
                var o = new JObject();
                o["id"] = (int)f.ID;
                o["name"] = StateName(f);
                Put(o, "alien", delegate { return (JToken)f.IsAlienFaction; });
                // isActivePlayer reads GameControl.control, which is torn down during a
                // scene swap.
                Put(o, "activePlayer", delegate { return (JToken)f.isActivePlayer; });
                o["resources"] = Resources(f);
                Put(o, "estimatedAlienHate", delegate { return Num(f.GetEstimatedAlienHate()); });
                a.Add(o);
            }
            return a;
        }

        static JToken Resources(TIFactionState f)
        {
            var o = new JObject();
            Dictionary<FactionResource, float> copy;
            try { copy = f.copyResources; }
            catch (Exception) { return null; }
            if (copy == null) return null;
            foreach (KeyValuePair<FactionResource, float> kv in copy)
                o[kv.Key.ToString()] = Num(kv.Value);
            return o;
        }

        static JToken QueryHabs(JObject args)
        {
            int factionFilter = OptionalInt(args, "faction");
            var a = new JArray();
            foreach (TIHabState hab in GameStateManager.IterateByClass<TIHabState>(false))
            {
                if (hab == null) continue;
                TIFactionState owner = FactionOf(hab);
                if (factionFilter >= 0 && (owner == null || (int)owner.ID != factionFilter)) continue;
                var o = new JObject();
                o["id"] = (int)hab.ID;
                o["name"] = StateName(hab);
                o["tier"] = hab.tier;
                o["type"] = hab.habType.ToString();
                o["faction"] = owner != null ? Describe(owner) : null;
                o["location"] = Where(hab);
                Put(o, "moduleCount", delegate { return (JToken)hab.numCompletedModules; });
                a.Add(o);
            }
            return a;
        }

        // Site first: a hab that sits on a body reports the body's site, and only a
        // free-flying one falls back to its orbit.
        static string Where(TIHabState hab)
        {
            try
            {
                TIGameState site = hab.ref_habSite;
                if (site != null) return StateName(site);
            }
            catch (Exception) { }
            try
            {
                TIGameState orbit = hab.ref_orbit;
                if (orbit != null) return StateName(orbit);
            }
            catch (Exception) { }
            return null;
        }

        static JToken QueryFleets(JObject args)
        {
            int factionFilter = OptionalInt(args, "faction");
            var a = new JArray();
            foreach (TISpaceFleetState fleet in GameStateManager.IterateByClass<TISpaceFleetState>(false))
            {
                if (fleet == null) continue;
                TIFactionState owner = FactionOf(fleet);
                if (factionFilter >= 0 && (owner == null || (int)owner.ID != factionFilter)) continue;
                var o = new JObject();
                o["id"] = (int)fleet.ID;
                o["name"] = StateName(fleet);
                o["faction"] = owner != null ? Describe(owner) : null;
                string location = null;
                try
                {
                    TIGameState at = fleet.location;
                    if (at != null) location = StateName(at);
                }
                catch (Exception) { }
                o["location"] = location;
                Put(o, "ships", delegate { return (JToken)(fleet.ships != null ? fleet.ships.Count : 0); });
                o["inCombat"] = fleet.inCombat;
                Put(o, "inCombatOrWaiting", delegate { return (JToken)fleet.inCombatOrWaitingForCombat; });
                a.Add(o);
            }
            return a;
        }

        static TIFactionState FactionOf(TIGameState state)
        {
            return Safe<TIFactionState>(delegate { return state.ref_faction; }, null);
        }

        // A path longer than this is a graph query, which this verb is not.
        const int MaxPathSegments = 16;

        // One dot-separated step: a member name and at most one list index.
        class Segment
        {
            public string name;
            public int index;
        }

        static JToken QueryState(JObject args)
        {
            bool includePrivate = Flag(args, "include_private");
            string root = Str(args, "root");
            JToken idToken = args != null ? args["id"] : null;
            bool hasId = idToken != null && idToken.Type != JTokenType.Null;
            bool hasRoot = !string.IsNullOrEmpty(root);
            if (hasId && hasRoot)
                throw new VerbError("pass 'id' or 'root', not both");
            if (!hasId && !hasRoot)
                throw new VerbError("pass 'id' (a game state id) or "
                    + "'root' (a static entry point, e.g. 'GameControl.control')");

            string pathText = Str(args, "path");
            object current = hasId
                ? (object)ById<TIGameState>(Int(args, "id"))
                : Root(root, includePrivate);
            current = Walk(current, ParsePath(pathText), includePrivate, "", 0);

            var o = new JObject();
            o["root"] = hasRoot ? (JToken)new JValue(root) : JValue.CreateNull();
            o["path"] = !string.IsNullOrEmpty(pathText)
                ? (JToken)new JValue(pathText) : JValue.CreateNull();
            o["type"] = current != null
                ? (JToken)new JValue(current.GetType().Name) : JValue.CreateNull();

            var state = current as TIGameState;
            if (state != null)
            {
                o["id"] = (int)state.ID;
                o["name"] = StateName(state);
                o["members"] = Members(state, includePrivate);
                return o;
            }
            if (current == null)
            {
                o["value"] = JValue.CreateNull();
                return o;
            }
            // Anything Value() or Element() renders whole is a value; everything else
            // gets the same member dump a game state gets.
            JToken scalar = ScalarToken(current);
            if (scalar != null)
            {
                o["value"] = scalar;
                return o;
            }
            JToken list = ListValue(current, current.GetType());
            if (list != null)
            {
                o["value"] = list;
                return o;
            }
            o["members"] = Members(current, includePrivate);
            return o;
        }

        // The endpoint dump, identical in shape whatever the walk landed on.
        static JObject Members(object target, bool includePrivate)
        {
            var members = new JObject();
            List<MemberInfo> list = ReadableMembers(target.GetType(), includePrivate);
            for (int i = 0; i < list.Count; i++)
            {
                MemberInfo m = list[i];
                if (IsObsolete(m)) continue;
                FieldInfo f = m as FieldInfo;
                if (f != null)
                {
                    members[f.Name] = Read(target, f, null);
                    continue;
                }
                PropertyInfo p = (PropertyInfo)m;
                // A public property can still hide its getter.
                if (p.GetGetMethod(includePrivate) == null) continue;
                members[p.Name] = Read(target, null, p);
            }
            return members;
        }

        // Values rendered whole rather than dumped member by member. Templates collapse
        // to a data name, as they do inside an expanded list.
        static JToken ScalarToken(object v)
        {
            var template = v as TIDataTemplate;
            if (template != null)
            {
                return Safe<JToken>(
                    delegate { return new JValue(template.dataName); }, JValue.CreateNull());
            }
            Type t = v.GetType();
            if (v is string || v is TIDateTime || t.IsEnum) return Value(v);
            switch (Type.GetTypeCode(t))
            {
                case TypeCode.Boolean:
                case TypeCode.Char:
                case TypeCode.SByte:
                case TypeCode.Byte:
                case TypeCode.Int16:
                case TypeCode.UInt16:
                case TypeCode.Int32:
                case TypeCode.UInt32:
                case TypeCode.Int64:
                case TypeCode.UInt64:
                case TypeCode.Single:
                case TypeCode.Double:
                case TypeCode.Decimal:
                    return Value(v);
            }
            return null;
        }

        #region Path walking

        // The cap is applied to the split, before a single segment is parsed: the line
        // reader accepts a megabyte, and anything that walks or resolves per part has to
        // be kept away from a path with half a million of them.
        static List<Segment> ParsePath(string path)
        {
            var segments = new List<Segment>();
            if (string.IsNullOrEmpty(path)) return segments;
            string[] parts = path.Split('.');
            if (parts.Length > MaxPathSegments)
                throw new VerbError("path has " + parts.Length + " segments; at most "
                    + MaxPathSegments);
            for (int i = 0; i < parts.Length; i++) segments.Add(ParseSegment(parts[i]));
            return segments;
        }

        // name, or name[n]. One indexer per segment: a second would need the element
        // type resolved mid-parse for no gain over splitting the path.
        static Segment ParseSegment(string text)
        {
            string part = text != null ? text.Trim() : "";
            if (part.Length == 0) throw new VerbError("empty segment in path");
            var seg = new Segment();
            seg.index = -1;
            int open = part.IndexOf('[');
            if (open >= 0)
            {
                if (part[part.Length - 1] != ']')
                    throw new VerbError("segment '" + part + "' has a malformed indexer");
                // Caught here so "a[1][2]" reports the real problem instead of failing
                // as a malformed integer.
                if (part.IndexOf('[', open + 1) >= 0)
                    throw new VerbError("segment '" + text + "' carries more than one "
                        + "indexer; split it across two segments");
                string inner = part.Substring(open + 1, part.Length - open - 2);
                int index;
                if (!int.TryParse(inner, NumberStyles.Integer, CultureInfo.InvariantCulture,
                        out index) || index < 0)
                    throw new VerbError("segment '" + part
                        + "' needs a non-negative integer index");
                seg.index = index;
                part = part.Substring(0, open).Trim();
                if (part.Length == 0)
                    throw new VerbError("segment '" + text + "' has an indexer but no member name");
                if (part.IndexOf('[') >= 0 || part.IndexOf(']') >= 0)
                    throw new VerbError("segment '" + text + "' carries more than one indexer");
            }
            else if (part.IndexOf(']') >= 0)
            {
                throw new VerbError("segment '" + part + "' has a malformed indexer");
            }
            seg.name = part;
            return seg;
        }

        // One step per segment, each read guarded. A failed step names the segment and
        // the type it failed on rather than answering null, because a null answer and a
        // wrong member name look identical to the caller.
        // offset continues the numbering a caller has already started, so the segments
        // walked out of a root follow the static member rather than restarting at 1.
        static object Walk(object current, List<Segment> segments, bool includePrivate,
            string where, int offset)
        {
            for (int i = 0; i < segments.Count; i++)
            {
                Segment seg = segments[i];
                int n = offset + i + 1;
                if (current == null)
                    throw new VerbError("cannot read '" + seg.name + "' at " + where
                        + "segment " + n + ": the previous step was null");
                Type type = current.GetType();
                MemberInfo member = FindMember(type, seg.name, includePrivate, false);
                if (member == null)
                    throw new VerbError("no member '" + seg.name + "' on " + type.Name
                        + " at " + where + "segment " + n);
                current = ReadMember(current, member, seg.name, type, where, n);
                if (seg.index >= 0) current = Index(current, seg, where, n);
            }
            return current;
        }

        static object ReadMember(object target, MemberInfo member, string name, Type type,
            string where, int n)
        {
            try
            {
                FieldInfo f = member as FieldInfo;
                if (f != null) return f.GetValue(f.IsStatic ? null : target);
                var p = (PropertyInfo)member;
                MethodInfo getter = p.GetGetMethod(true);
                if (getter == null)
                    throw new VerbError("member '" + name + "' on " + type.Name
                        + " at " + where + "segment " + n + " has no getter");
                return p.GetValue(getter.IsStatic ? null : target, null);
            }
            catch (VerbError) { throw; }
            catch (TargetInvocationException e)
            {
                Exception inner = e.InnerException != null ? e.InnerException : e;
                throw new VerbError("reading '" + name + "' on " + type.Name + " at "
                    + where + "segment " + n + " threw " + Note(inner));
            }
            catch (Exception e)
            {
                throw new VerbError("reading '" + name + "' on " + type.Name + " at "
                    + where + "segment " + n + " threw " + Note(e));
            }
        }

        // Lists and arrays only. A dictionary needs a key rather than an index, and
        // guessing which key type was meant is worse than refusing.
        static object Index(object current, Segment seg, string where, int n)
        {
            if (current == null)
                throw new VerbError("'" + seg.name + "' at " + where + "segment " + n
                    + " is null and cannot be indexed");
            var list = current as System.Collections.IList;
            if (list == null)
                throw new VerbError("'" + seg.name + "' at " + where + "segment " + n
                    + " is a " + current.GetType().Name + ", not a list or array");
            int count = Safe<int>(delegate { return list.Count; }, -1);
            if (count < 0)
                throw new VerbError("'" + seg.name + "' at " + where + "segment " + n
                    + " would not report a count");
            if (seg.index >= count)
                throw new VerbError("index " + seg.index + " is out of range for '"
                    + seg.name + "' at " + where + "segment " + n + " (" + count + " entries)");
            try { return list[seg.index]; }
            catch (Exception e)
            {
                throw new VerbError("indexing '" + seg.name + "' at " + where + "segment "
                    + n + " threw " + Note(e));
            }
        }

        // Fields before properties: a property getter runs game code, and some of them
        // mutate. Private members are not inherited by reflection, so the base chain is
        // walked by hand when they are asked for.
        static MemberInfo FindMember(Type type, string name, bool includePrivate, bool wantStatic)
        {
            BindingFlags scope = wantStatic ? BindingFlags.Static : BindingFlags.Instance;
            MemberInfo found = Lookup(type, name, BindingFlags.Public | scope);
            if (found == null)
            {
                found = Lookup(type, name,
                    BindingFlags.Public | scope | BindingFlags.IgnoreCase);
            }
            if (found != null || !includePrivate) return found;

            BindingFlags priv = BindingFlags.NonPublic | scope | BindingFlags.DeclaredOnly;
            for (Type t = type; t != null; t = t.BaseType)
            {
                found = Lookup(t, name, priv);
                if (found == null) found = Lookup(t, name, priv | BindingFlags.IgnoreCase);
                if (found != null) return found;
            }
            return null;
        }

        // A member re-declared with a narrower type in a subclass leaves two of that name
        // in the hierarchy, and reflection refuses to pick: TIHabState, TISpaceBodyState,
        // TILagrangePointState and TISpaceFleetState all narrow TISpaceObjectState's
        // template that way. Swallowing that would answer "no member 'template'" for
        // something the dump plainly lists, so the ambiguity is resolved the way the dump
        // resolves it, by taking the most derived declaration.
        static MemberInfo Lookup(Type type, string name, BindingFlags flags)
        {
            try
            {
                FieldInfo f = type.GetField(name, flags);
                if (f != null) return f;
            }
            catch (AmbiguousMatchException)
            {
                MemberInfo hit = MostDerived(type, name, flags, true);
                if (hit != null) return hit;
            }
            catch (Exception) { }

            try
            {
                PropertyInfo p = type.GetProperty(name, flags);
                return p != null && p.GetIndexParameters().Length == 0 ? (MemberInfo)p : null;
            }
            catch (AmbiguousMatchException)
            {
                return MostDerived(type, name, flags, false);
            }
            catch (Exception) { return null; }
        }

        // Down from the type itself, one declaring type at a time, so the first hit is
        // the most derived one.
        static MemberInfo MostDerived(Type type, string name, BindingFlags flags, bool field)
        {
            BindingFlags declared = flags | BindingFlags.DeclaredOnly;
            for (Type t = type; t != null; t = t.BaseType)
            {
                Type declaring = t;
                MemberInfo hit = Safe<MemberInfo>(delegate
                {
                    if (field) return declaring.GetField(name, declared);
                    PropertyInfo p = declaring.GetProperty(name, declared);
                    return p != null && p.GetIndexParameters().Length == 0 ? (MemberInfo)p : null;
                }, null);
                if (hit != null) return hit;
            }
            return null;
        }

        // "TypeName.Member" or "TypeName.Member.Member...". A namespace-qualified type
        // name carries dots of its own, so the longest prefix that resolves to a type
        // wins and everything after it is walked as members, the first one static.
        static object Root(string root, bool includePrivate)
        {
            string[] parts = root.Split('.');
            if (parts.Length < 2)
                throw new VerbError("arg 'root' must be 'TypeName.Member', e.g. "
                    + "'GameControl.control'");
            // Before any resolution: every prefix that misses costs a sweep over every
            // loaded assembly, the prefixes are distinct so the cache cannot help within
            // one call, and the queue drains on the main thread. A root with hundreds of
            // dots would hang the game rather than fail.
            if (parts.Length > MaxPathSegments)
                throw new VerbError("root has " + parts.Length
                    + " dot-separated parts; at most " + MaxPathSegments);
            for (int cut = parts.Length - 1; cut >= 1; cut--)
            {
                Type type = GameType(string.Join(".", parts, 0, cut));
                if (type == null) continue;

                Segment first = ParseSegment(parts[cut]);
                MemberInfo member = FindMember(type, first.name, includePrivate, true);
                if (member == null)
                    throw new VerbError("no static member '" + first.name + "' on "
                        + type.Name);
                object current = ReadMember(null, member, first.name, type, "root ", 1);
                if (first.index >= 0) current = Index(current, first, "root ", 1);

                var rest = new List<Segment>();
                for (int i = cut + 1; i < parts.Length; i++)
                    rest.Add(ParseSegment(parts[i]));
                // The static member was segment 1, so the rest continue from there.
                return Walk(current, rest, includePrivate, "root ", 1);
            }
            throw new VerbError("no type in the loaded assemblies matches any prefix of "
                + "root '" + root + "'");
        }

        // Resolved names are cached, misses included: the by-name sweep walks every type
        // in every loaded assembly and must not run twice for the same miss.
        static readonly Dictionary<string, Type> rootTypes =
            new Dictionary<string, Type>(StringComparer.Ordinal);

        static Type GameType(string name)
        {
            Type cached;
            if (rootTypes.TryGetValue(name, out cached)) return cached;
            Type found = FindGameType(name);
            rootTypes[name] = found;
            return found;
        }

        static Type FindGameType(string name)
        {
            // Assembly-CSharp first, and a bare name is retried inside the game's own
            // namespace, where most of it lives.
            Assembly game = typeof(TIGameState).Assembly;
            Type t = LoadType(game, name);
            if (t == null) t = LoadType(game, "PavonisInteractive.TerraInvicta." + name);
            if (t != null) return t;

            Assembly[] loaded;
            try { loaded = AppDomain.CurrentDomain.GetAssemblies(); }
            catch (Exception) { return null; }
            for (int i = 0; i < loaded.Length; i++)
            {
                t = LoadType(loaded[i], name);
                if (t != null) return t;
            }
            // A bare class name in a namespace nobody guessed still resolves.
            for (int i = 0; i < loaded.Length; i++)
            {
                Type[] types;
                try { types = loaded[i].GetTypes(); }
                catch (Exception) { continue; }
                for (int j = 0; j < types.Length; j++)
                {
                    if (string.Equals(types[j].Name, name, StringComparison.Ordinal))
                        return types[j];
                }
            }
            return null;
        }

        #endregion

        // Getters run arbitrary game code, and plenty of them assume a live visualizer,
        // so a throwing member is reported in place and the dump continues.
        static JToken Read(object target, FieldInfo field, PropertyInfo prop)
        {
            try
            {
                object v = field != null ? field.GetValue(target) : prop.GetValue(target, null);
                return Value(v);
            }
            catch (TargetInvocationException e)
            {
                Exception inner = e.InnerException != null ? e.InnerException : e;
                return new JValue("<error: " + inner.GetType().Name + ">");
            }
            catch (Exception e)
            {
                return new JValue("<error: " + e.GetType().Name + ">");
            }
        }

        static bool IsObsolete(MemberInfo m)
        {
            return Safe<bool>(delegate { return m.IsDefined(typeof(ObsoleteAttribute), true); }, false);
        }

        // Depth 1: scalars verbatim, game states collapsed, anything else named.
        static JToken Value(object v)
        {
            if (v == null) return JValue.CreateNull();

            var gs = v as TIGameState;
            if (gs != null) return Describe(gs);

            var date = v as TIDateTime;
            if (date != null) return new JValue(InvariantDate(date));

            var s = v as string;
            if (s != null) return new JValue(s);

            Type t = v.GetType();
            if (t.IsEnum) return new JValue(v.ToString());

            switch (Type.GetTypeCode(t))
            {
                case TypeCode.Boolean:
                    return new JValue((bool)v);
                case TypeCode.Char:
                    return new JValue(v.ToString());
                case TypeCode.SByte:
                case TypeCode.Byte:
                case TypeCode.Int16:
                case TypeCode.UInt16:
                case TypeCode.Int32:
                case TypeCode.UInt32:
                case TypeCode.Int64:
                    return new JValue(Convert.ToInt64(v, CultureInfo.InvariantCulture));
                case TypeCode.UInt64:
                    return new JValue(Convert.ToUInt64(v, CultureInfo.InvariantCulture));
                case TypeCode.Single:
                    return Num((float)v);
                case TypeCode.Double:
                case TypeCode.Decimal:
                    return Num(Convert.ToDouble(v, CultureInfo.InvariantCulture));
            }

            JToken list = ListValue(v, t);
            if (list != null) return list;
            return new JValue(t.Name);
        }

        // Lists and single-dimension arrays expand when their declared element type is
        // something depth 1 can render: a game state, a template, a string, an enum, or
        // a number or bool. A dictionary, a nested list, or a collection of arbitrary
        // objects stays a type name, which is what keeps one member from dragging in
        // the rest of the graph. Over-long lists are cut at the cap without a marker,
        // as this has always done.
        static JToken ListValue(object v, Type t)
        {
            Type element = null;
            if (t.IsArray && t.GetArrayRank() == 1) element = t.GetElementType();
            else if (t.IsGenericType && t.GetGenericTypeDefinition() == typeof(List<>))
                element = t.GetGenericArguments()[0];
            if (element == null || !Expandable(element)) return null;

            var items = v as System.Collections.IEnumerable;
            if (items == null) return null;
            var a = new JArray();
            int n = 0;
            foreach (object item in items)
            {
                if (n >= MaxRefListLength) break;
                n++;
                a.Add(Element(item));
            }
            return a;
        }

        // The declared element type decides, so a list is expanded or named as a whole
        // rather than per entry.
        static bool Expandable(Type element)
        {
            if (typeof(TIGameState).IsAssignableFrom(element)) return true;
            if (typeof(TIDataTemplate).IsAssignableFrom(element)) return true;
            if (element == typeof(string) || element.IsEnum) return true;
            switch (Type.GetTypeCode(element))
            {
                case TypeCode.Boolean:
                case TypeCode.SByte:
                case TypeCode.Byte:
                case TypeCode.Int16:
                case TypeCode.UInt16:
                case TypeCode.Int32:
                case TypeCode.UInt32:
                case TypeCode.Int64:
                case TypeCode.UInt64:
                case TypeCode.Single:
                case TypeCode.Double:
                case TypeCode.Decimal:
                    return true;
            }
            return false;
        }

        // Templates collapse to their data name, the handle every other verb takes;
        // everything else an expandable list can hold is already a Value case. The
        // name is read through a guard because it is a property like any other.
        static JToken Element(object item)
        {
            if (item == null) return JValue.CreateNull();
            var template = item as TIDataTemplate;
            if (template != null)
            {
                // The error string keeps a throwing dataName getter distinguishable
                // from a genuinely null entry, matching the dump's <error: X> idiom.
                return Safe<JToken>(
                    delegate { return new JValue(template.dataName); },
                    new JValue("<error: dataName>"));
            }
            return Value(item);
        }

        // NaN and the infinities are not JSON numbers; they ride as strings so a client
        // parser never chokes on a response. Floats stay floats so the widening noise of
        // a double cast stays out of the output.
        static JToken Num(float f)
        {
            if (float.IsNaN(f) || float.IsInfinity(f))
                return new JValue(f.ToString(CultureInfo.InvariantCulture));
            return new JValue(f);
        }

        static JToken Num(double d)
        {
            if (double.IsNaN(d) || double.IsInfinity(d))
                return new JValue(d.ToString(CultureInfo.InvariantCulture));
            return new JValue(d);
        }

        static JToken TimePause(JObject args)
        {
            Manager().Pause();
            clockParkedByDriver = true;
            return QueryTime(args);
        }

        // A refusal, not a failure: answering with the time state plus a named
        // hold keeps one call enough to learn why the clock is paused.
        // Unreachable today -- HoldActive is always false since the engagement
        // stopped holding the clock for mission phases -- but the gate and the
        // reply shape stay so no caller has to reason about a removed path.
        static JToken Held()
        {
            JToken data = QueryTime(new JObject());
            var o = data as JObject;
            if (o != null) o["held"] = AiControl.HoldReason;
            return data;
        }

        static JToken TimePlay(JObject args)
        {
            if (AiControl.HoldActive) return Held();
            clockParkedByDriver = false;
            Manager().Play();
            return QueryTime(args);
        }

        static JToken TimeSpeed(JObject args)
        {
            int level = Int(args, "level");
            if (level < 0) throw new VerbError("arg 'level' must be >= 0");
            GameTimeManager manager = Manager();
            int count = -1;
            try
            {
                var speeds = manager.currentSpeeds;
                if (speeds != null) count = speeds.Count;
            }
            catch (Exception) { }
            if (count > 0 && level >= count)
                throw new VerbError("arg 'level' must be < " + count);
            // After validation: a bad level is a bad level whether or not the
            // engagement is holding the clock.
            if (AiControl.HoldActive) return Held();
            clockParkedByDriver = level == 0;
            manager.SetSpeed(level, false);
            return QueryTime(args);
        }

        static GameTimeManager Manager()
        {
            GameTimeManager manager = GameTimeManager.Singleton;
            if (manager == null) throw new VerbError("no time manager");
            return manager;
        }

        // Armed target for time.run_until. Held as a bare date, never a game object, so
        // a scene reload between frames leaves nothing stale behind.
        static TIDateTime runUntilTarget;
        static string runUntilText;

        // True only when the wire stopped the clock on purpose: time.pause,
        // time.speed level 0, or a fired run_until park. Vanilla's own
        // pauseTime events (councilor phase starts and the like) never set it,
        // which is how the engaged tick tells a requested stop from a pause
        // that expects a human -- it honors the former and resumes through the
        // latter (AiControl.ResumeThroughPause).
        static bool clockParkedByDriver;
        internal static bool ClockParkedByDriver { get { return clockParkedByDriver; } }

        static JToken TimeRunUntil(JObject args)
        {
            string text = Str(args, "date");
            if (string.IsNullOrEmpty(text)) throw new VerbError("missing arg 'date'");
            DateTime parsed;
            try
            {
                parsed = DateTime.ParseExact(text, "yyyy-MM-dd", CultureInfo.InvariantCulture);
            }
            catch (Exception)
            {
                throw new VerbError("arg 'date' must be yyyy-MM-dd");
            }
            var target = new TIDateTime(parsed.Year, parsed.Month, parsed.Day);
            runUntilTarget = target;
            runUntilText = InvariantDate(target);
            // Arming is a statement of intent to run; a stale park must not
            // leave the engaged tick refusing to resume.
            clockParkedByDriver = false;
            var o = new JObject();
            o["armed"] = true;
            o["target"] = runUntilText;
            return o;
        }

        // Main thread, once a frame. Each armed job is isolated so a failure in one does
        // not stop the other from being serviced.
        public static void Tick()
        {
            try { TickRunUntil(); }
            catch (Exception e) { Server.LastError = Note(e); }
            TickAutoresolve();
            // An engagement must not survive the campaign it was made in.
            try { AiControl.Tick(); }
            catch (Exception e) { Server.LastError = Note(e); }
        }

        // Disarms before pausing so a failed Pause cannot leave a target that fires forever.
        static void TickRunUntil()
        {
            TIDateTime target = runUntilTarget;
            if (target == null) return;
            if (!HasCampaign) return;
            GameTimeManager manager = GameTimeManager.Singleton;
            if (manager == null) return;
            TIDateTime now = manager.currentTime;
            if (now == null) return;
            if (now < target) return;
            runUntilTarget = null;
            runUntilText = null;
            // Parked before Pause: AiControl.Tick runs later this same frame
            // and must already see the park, or it resumes right through the
            // stop the driver asked for.
            clockParkedByDriver = true;
            manager.Pause();
        }

        // Which extension the game writes depends on a profile setting, so both are listed.
        static readonly string[] SaveExtensions = new string[] { ".gz", ".json" };

        static JToken SavesList(JObject args)
        {
            string folder = SaveFolder();
            var a = new JArray();
            if (!Directory.Exists(folder)) return a;
            for (int e = 0; e < SaveExtensions.Length; e++)
            {
                string[] paths = Directory.GetFiles(folder, "*" + SaveExtensions[e]);
                Array.Sort(paths, StringComparer.Ordinal);
                for (int i = 0; i < paths.Length; i++)
                {
                    var o = new JObject();
                    o["name"] = Path.GetFileNameWithoutExtension(paths[i]);
                    o["extension"] = SaveExtensions[e];
                    try
                    {
                        o["mtime"] = File.GetLastWriteTime(paths[i])
                            .ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
                    }
                    catch (Exception) { o["mtime"] = null; }
                    a.Add(o);
                }
            }
            return a;
        }

        static JToken SavesSave(JObject args)
        {
            string name = SaveName(args);
            // Every vanilla save path asks this first: a save-blocking prompt or an
            // autoresolving combat means the gamestate is mid-change.
            if (SaveMenuController.SavingIsBlocked()) throw new VerbError("saving blocked");
            string path = TIUtilities.GetSaveFilePath(name);
            if (!GameStateManager.SaveAllGameStates(path, true))
                throw new VerbError("save failed");
            var o = new JObject();
            o["path"] = path;
            return o;
        }

        static JToken SavesLoad(JObject args)
        {
            string name = SaveName(args);
            string path = ExistingSavePath(name);
            if (path == null) throw new VerbError("no save named '" + name + "'");

            // No Singleton on this controller; the component lives on the pause menu
            // inside the running scene, which is inactive.
            LoadMenuController menu = Require<LoadMenuController>("load menu controller");
            if (menu.importMode) throw new VerbError("load menu is in import mode");
            if (menu.loadingScreen == null) throw new VerbError("load menu has no loading screen");
            // LoadSaveFilePath destroys the campaign's view data before it touches its
            // scene manager, so every reason to refuse has to be settled up front.
            EnsureSceneManager(menu);

            // Anything armed against the outgoing campaign must not act on the incoming
            // one. State ids are reused across campaigns, so an armed autoresolve would
            // happily bind whatever combat inherits the id and start submitting stances.
            runUntilTarget = null;
            runUntilText = null;
            clockParkedByDriver = false;
            Disarm();
            autoError = null;
            autoNote = null;
            menu.LoadSaveFilePath(path);

            var o = new JObject();
            o["loading"] = true;
            o["path"] = path;
            return o;
        }

        static readonly FieldInfo sceneManagerField = SceneManagerField();

        static FieldInfo SceneManagerField()
        {
            try
            {
                return typeof(LoadMenuController).GetField("sceneManager",
                    BindingFlags.NonPublic | BindingFlags.Instance);
            }
            catch (Exception) { return null; }
        }

        // The menu's scene manager is injected from Unity's Start, which never runs on an
        // object that has stayed inactive since the scene loaded. Backfill it the same way
        // Start would.
        static void EnsureSceneManager(LoadMenuController menu)
        {
            if (sceneManagerField == null) throw new VerbError("no sceneManager field on load menu");
            if (sceneManagerField.GetValue(menu) != null) return;

            SceneManager scenes = null;
            try
            {
                var container = SolarSystemInstaller.container;
                if (container != null) scenes = container.Resolve<SceneManager>();
            }
            catch (Exception) { }
            if (scenes == null)
            {
                try { scenes = SceneManager.self; }
                catch (Exception) { }
            }
            if (scenes == null) throw new VerbError("no scene manager");
            sceneManagerField.SetValue(menu, scenes);
        }

        static string SaveFolder()
        {
            string folder = CreateSaveFileScrollList.GetSaveFolderPath();
            if (string.IsNullOrEmpty(folder)) throw new VerbError("no save folder");
            return folder;
        }

        // The game writes through Wine, where these still name devices rather than files:
        // a save to one of them reports success and leaves nothing on disk.
        static readonly string[] ReservedNames = new string[] {
            "CON", "PRN", "AUX", "NUL",
            "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
            "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9"
        };

        // Save names address one folder, so anything that could walk out of it is refused.
        static string SaveName(JObject args)
        {
            string name = Str(args, "name");
            if (string.IsNullOrEmpty(name)) throw new VerbError("missing arg 'name'");
            name = name.Trim();
            if (name.Length == 0) throw new VerbError("arg 'name' is empty");
            if (name.IndexOf('/') >= 0 || name.IndexOf('\\') >= 0
                || name.IndexOf(':') >= 0 || name.IndexOf("..") >= 0)
                throw new VerbError("arg 'name' must be a plain file name");

            // A device name claims every extension, so the stem is what has to be checked.
            int dot = name.IndexOf('.');
            string stem = dot >= 0 ? name.Substring(0, dot) : name;
            for (int i = 0; i < ReservedNames.Length; i++)
            {
                if (string.Equals(stem, ReservedNames[i], StringComparison.OrdinalIgnoreCase))
                    throw new VerbError("arg 'name' is a reserved device name");
            }
            return name;
        }

        // The configured extension depends on a profile setting, so both are tried.
        static string ExistingSavePath(string name)
        {
            string preferred = TIUtilities.GetSaveFilePath(name);
            if (File.Exists(preferred)) return preferred;
            string folder = SaveFolder();
            for (int i = 0; i < SaveExtensions.Length; i++)
            {
                string path = Path.Combine(folder, name + SaveExtensions[i]);
                if (File.Exists(path)) return path;
            }
            return null;
        }

        // TIDateTime.ToString(string) forwards to DateTime.ToString(string), which picks up
        // the current culture's calendar and digits. The date fields are plain ints, so the
        // frame is built from them under the invariant culture.
        static string InvariantDate(TIDateTime t)
        {
            return string.Format(CultureInfo.InvariantCulture,
                "{0:D4}-{1:D2}-{2:D2} {3:D2}:{4:D2}:{5:D2}",
                t.year, t.month, t.day, t.hour, t.minute, t.second);
        }

        static JToken Describe(TIGameState state)
        {
            var o = new JObject();
            o["id"] = (int)state.ID;
            o["type"] = state.GetType().Name;
            o["name"] = StateName(state);
            return o;
        }

        static string StateName(TIGameState state)
        {
            string name = null;
            try
            {
                GameControl control = GameControl.control;
                name = state.GetDisplayName(control != null ? control.activePlayer : null);
            }
            catch (Exception) { }
            if (string.IsNullOrEmpty(name))
            {
                try { name = state.displayName; }
                catch (Exception) { }
            }
            return name;
        }

        static JArray ToArray(List<string> lines)
        {
            return ToArray(lines, int.MaxValue);
        }

        static JArray ToArray(List<string> lines, int max)
        {
            var a = new JArray();
            if (lines == null) return a;
            int n = lines.Count < max ? lines.Count : max;
            for (int i = 0; i < n; i++) a.Add(new JValue(lines[i]));
            return a;
        }

        static string Str(JObject args, string key)
        {
            JToken t = args != null ? args[key] : null;
            if (t == null || t.Type == JTokenType.Null) return null;
            return t.Type == JTokenType.String ? (string)t : t.ToString();
        }

        static int Int(JObject args, string key)
        {
            JToken t = args != null ? args[key] : null;
            if (t == null || t.Type == JTokenType.Null) throw new VerbError("missing arg '" + key + "'");
            try { return (int)t; }
            catch (Exception) { throw new VerbError("arg '" + key + "' must be an integer"); }
        }

        // Filter args are absent far more often than not; -1 means "no filter", and
        // state IDs are never negative.
        static int OptionalInt(JObject args, string key)
        {
            JToken t = args != null ? args[key] : null;
            if (t == null || t.Type == JTokenType.Null) return -1;
            try { return (int)t; }
            catch (Exception) { throw new VerbError("arg '" + key + "' must be an integer"); }
        }

        #region Shared helpers

        // Guarded member write: a throwing read records null so the rest of the
        // response survives.
        static void Put(JObject o, string key, Func<JToken> read)
        {
            try { o[key] = read(); }
            catch (Exception) { o[key] = JValue.CreateNull(); }
        }

        static T Safe<T>(Func<T> read, T fallback)
        {
            try { return read(); }
            catch (Exception) { return fallback; }
        }

        static T ById<T>(int id) where T : TIGameState
        {
            T state = GameStateManager.FindGameState<T>(new GameStateID(id), true);
            if (state == null) throw new VerbError("no " + typeof(T).Name + " with id " + id);
            return state;
        }

        static T Arg<T>(JObject args, string key) where T : TIGameState
        {
            return ById<T>(Int(args, key));
        }

        // Inactive objects included: several controllers live on disabled canvases.
        static T Find<T>() where T : UnityEngine.Component
        {
            try { return UnityEngine.Object.FindObjectOfType<T>(true); }
            catch (Exception) { return null; }
        }

        static T Require<T>(string what) where T : UnityEngine.Component
        {
            T found = Find<T>();
            if (found == null) throw new VerbError("no " + what);
            return found;
        }

        internal static string Note(Exception e)
        {
            return e.GetType().Name + ": " + e.Message;
        }

        // A button the game would accept a click on: live in the hierarchy, enabled,
        // interactable, and on an enabled canvas.
        static bool Clickable(UnityEngine.UI.Button button)
        {
            try
            {
                if (button == null || !button.gameObject.activeInHierarchy) return false;
                if (!button.enabled || !button.interactable) return false;
                UnityEngine.Canvas canvas = button.GetComponentInParent<UnityEngine.Canvas>();
                return canvas != null && canvas.enabled;
            }
            catch (Exception) { return false; }
        }

        static bool Clickable(UnityEngine.GameObject buttonObject)
        {
            try
            {
                if (buttonObject == null || !buttonObject.activeInHierarchy) return false;
                return Clickable(buttonObject.GetComponentInChildren<UnityEngine.UI.Button>());
            }
            catch (Exception) { return false; }
        }

        // Public instance fields, then the readable non-indexed public instance
        // properties. Callers apply their own attribute filters.
        static List<MemberInfo> ReadableMembers(Type type)
        {
            return ReadableMembers(type, false);
        }

        // With includePrivate, the non-public instance members follow, taken one
        // declaring type at a time because reflection does not inherit them. A private
        // member whose name a public one already took is skipped, so asking for more
        // never changes what the public dump reported.
        static List<MemberInfo> ReadableMembers(Type type, bool includePrivate)
        {
            var members = new List<MemberInfo>();
            var seen = new Dictionary<string, bool>(StringComparer.Ordinal);
            Collect(members, seen, type, BindingFlags.Public | BindingFlags.Instance);
            if (!includePrivate) return members;
            const BindingFlags priv = BindingFlags.NonPublic | BindingFlags.Instance
                | BindingFlags.DeclaredOnly;
            for (Type t = type; t != null; t = t.BaseType) Collect(members, seen, t, priv);
            return members;
        }

        static void Collect(List<MemberInfo> members, Dictionary<string, bool> seen,
            Type type, BindingFlags flags)
        {
            FieldInfo[] fields = Safe<FieldInfo[]>(
                delegate { return type.GetFields(flags); }, null);
            for (int i = 0; fields != null && i < fields.Length; i++)
            {
                if (seen.ContainsKey(fields[i].Name)) continue;
                seen[fields[i].Name] = true;
                members.Add(fields[i]);
            }
            PropertyInfo[] props = Safe<PropertyInfo[]>(
                delegate { return type.GetProperties(flags); }, null);
            for (int i = 0; props != null && i < props.Length; i++)
            {
                PropertyInfo p = props[i];
                if (!p.CanRead || p.GetIndexParameters().Length != 0) continue;
                if (seen.ContainsKey(p.Name)) continue;
                seen[p.Name] = true;
                members.Add(p);
            }
        }

        #endregion
    }
}

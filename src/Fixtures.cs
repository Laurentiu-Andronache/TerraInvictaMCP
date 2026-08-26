using System;
using System.Collections.Generic;
using System.Globalization;
using Newtonsoft.Json.Linq;
using PavonisInteractive.TerraInvicta;

namespace TerraInvictaMCP
{
    // nation.set_stat, kill.module.
    //
    // Both force state the way the spawn verbs do: they take the engine's own
    // mutator and leave out the play-legality around it. A fixture proves a
    // mechanism fires, never that the state is reachable through play.
    public static partial class Verbs
    {
        #region nation.set_stat

        // One core nation stat. `add` takes a DELTA, because every engine setter
        // for these is an AddTo* method and the backing property setters are all
        // private; set-to-value is AddTo* of (target - current).
        //
        // min/max are the engine's own clamp inside that AddTo*, and they are
        // enforced here rather than left to the clamp: AddToCohesion spills the
        // part that would drive cohesion below zero into democracy and unrest,
        // and AddToInequality spills the part above its maximum into cohesion and
        // unrest, so an out-of-range request would move stats the caller never
        // named. Refusing it keeps every write inside the range, where neither
        // spill can fire.
        class NationStat
        {
            public string name;
            public string reason;
            public float min;
            public float max;
            public Func<TINationState, float> read;
            public Action<TINationState, float> add;
        }

        static readonly List<NationStat> nationStats = BuildNationStats();

        static List<NationStat> BuildNationStats()
        {
            var list = new List<NationStat>();
            list.Add(Stat("cohesion", "CohesionReason_Effect", 0f, 10f,
                delegate(TINationState n) { return n.cohesion; },
                delegate(TINationState n, float d)
                {
                    n.AddToCohesion(d,
                        TINationState.CohesionChangeReason.CohesionReason_Effect);
                }));
            list.Add(Stat("democracy", "DemReason_EventEffect", 0f, 10f,
                delegate(TINationState n) { return n.democracy; },
                delegate(TINationState n, float d)
                {
                    n.AddToDemocracy(d,
                        TINationState.DemocracyChangeReason.DemReason_EventEffect);
                }));
            list.Add(Stat("inequality", "InqReason_EventEffects", 1f, 9f,
                delegate(TINationState n) { return n.inequality; },
                delegate(TINationState n, float d)
                {
                    n.AddToInequality(d,
                        TINationState.InequalityChangeReason.InqReason_EventEffects);
                }));
            list.Add(Stat("education", "EducationReason_EventEffect", 1f, 255f,
                delegate(TINationState n) { return n.education; },
                delegate(TINationState n, float d)
                {
                    n.AddToEducation(d,
                        TINationState.EducationChangeReason.EducationReason_EventEffect);
                }));
            return list;
        }

        static NationStat Stat(string name, string reason, float min, float max,
            Func<TINationState, float> read, Action<TINationState, float> add)
        {
            var s = new NationStat();
            s.name = name;
            s.reason = reason;
            s.min = min;
            s.max = max;
            s.read = read;
            s.add = add;
            return s;
        }

        // Stats the debug console already reaches. This verb refuses them and
        // names the command, rather than growing a second way to do the same
        // thing that a later engine change would have to keep in step. Each
        // entry carries the console command's own argument syntax.
        static readonly string[,] consoleStats = new string[,] {
            { "unrest", "ChangeUnrest <delta> (map selection: nation; a delta, not a target value)" },
            { "miltech", "SetMiltech <value>[, <nation dataName>]" },
            { "militarytechlevel", "SetMiltech <value>[, <nation dataName>]" },
            { "gdp", "ModifyGDP <nation>, <billions>" },
            { "sustainability", "SetSustainability <value> (map selection: nation)" },
            { "nukes", "SetNuclearWeapons <count> (map selection: nation)" },
            { "nuclearweapons", "SetNuclearWeapons <count> (map selection: nation)" },
        };

        static JToken NationSetStat(JObject args)
        {
            TINationState nation = ArgNation(args, "nation");
            string wanted = Str(args, "stat");
            if (string.IsNullOrEmpty(wanted))
                throw new VerbError("missing arg 'stat'; " + StatMenu());
            wanted = wanted.Trim();
            RefuseConsoleStat(wanted);

            NationStat stat = FindStat(wanted);
            if (stat == null)
                throw new VerbError("unknown stat '" + wanted + "'; " + StatMenu());

            float value = RequiredFloat(args, "value");
            if (float.IsNaN(value) || float.IsInfinity(value))
                throw new VerbError("arg 'value' must be a finite number");
            if (value < stat.min || value > stat.max)
                throw new VerbError(stat.name + " is clamped to "
                    + Range(stat) + " by the engine's own setter, and a request "
                    + "outside it would spill into other stats instead of being "
                    + "silently clamped; asked for "
                    + value.ToString(CultureInfo.InvariantCulture));

            JObject before = StatSnapshot(nation);
            float was = stat.read(nation);
            float delta = value - was;
            stat.add(nation, delta);
            JObject after = StatSnapshot(nation);

            var o = new JObject();
            o["nation"] = Describe(nation);
            o["stat"] = stat.name;
            o["requested"] = Num(value);
            o["before"] = Num(was);
            o["delta"] = Num(delta);
            // The read-back, not the request: AddToCohesion returns the delta it
            // was handed rather than the new value, and every one of these
            // re-clamps after adding, so the stat itself is the only truth.
            o["after"] = Safe<JToken>(
                delegate { return Num(stat.read(nation)); }, JValue.CreateNull());
            o["reason"] = stat.reason;
            o["range"] = Range(stat);
            o["stats"] = after;
            // Nothing else should have moved: the range check above keeps both
            // spill paths shut. Reported so a caller sees that rather than
            // trusting it.
            o["alsoChanged"] = Moved(before, after, stat.name);
            return o;
        }

        // Every core stat this verb or the console can move, so one call shows
        // whether anything besides the named stat shifted. unrest rides along
        // because it is the other side of both spill paths.
        static JObject StatSnapshot(TINationState nation)
        {
            var o = new JObject();
            for (int i = 0; i < nationStats.Count; i++)
            {
                NationStat stat = nationStats[i];
                o[stat.name] = Safe<JToken>(
                    delegate { return Num(stat.read(nation)); }, JValue.CreateNull());
            }
            Put(o, "unrest", delegate { return Num(nation.unrest); });
            return o;
        }

        static JArray Moved(JObject before, JObject after, string skip)
        {
            var a = new JArray();
            foreach (KeyValuePair<string, JToken> kv in before)
            {
                if (string.Equals(kv.Key, skip, StringComparison.Ordinal)) continue;
                JToken then = kv.Value;
                JToken now = after[kv.Key];
                if (JToken.DeepEquals(then, now)) continue;
                var row = new JObject();
                row["stat"] = kv.Key;
                row["before"] = then;
                row["after"] = now != null ? now : JValue.CreateNull();
                a.Add(row);
            }
            return a;
        }

        static NationStat FindStat(string name)
        {
            for (int i = 0; i < nationStats.Count; i++)
            {
                if (string.Equals(nationStats[i].name, name,
                        StringComparison.OrdinalIgnoreCase))
                    return nationStats[i];
            }
            return null;
        }

        static void RefuseConsoleStat(string name)
        {
            for (int i = 0; i < consoleStats.GetLength(0); i++)
            {
                if (!string.Equals(consoleStats[i, 0], name,
                        StringComparison.OrdinalIgnoreCase)) continue;
                throw new VerbError(name + " has a debug-console command and this "
                    + "verb does not duplicate it: run console line='"
                    + consoleStats[i, 1] + "'. This verb covers the core stats the "
                    + "console cannot reach: " + StatNames());
            }
        }

        static string StatMenu()
        {
            return "stat is one of " + StatNames()
                + "; unrest, miltech, gdp, sustainability and nukes have console "
                + "commands instead; ask for one and this verb names its command";
        }

        static string StatNames()
        {
            var names = new List<string>();
            for (int i = 0; i < nationStats.Count; i++)
                names.Add(nationStats[i].name + " " + Range(nationStats[i]));
            return string.Join(", ", names.ToArray());
        }

        static string Range(NationStat stat)
        {
            return "[" + stat.min.ToString(CultureInfo.InvariantCulture) + ".."
                + stat.max.ToString(CultureInfo.InvariantCulture) + "]";
        }

        #endregion

        #region kill.module

        // Modules listed in a refusal before the list is cut.
        const int MaxModulesListed = 40;

        static JToken KillModule(JObject args)
        {
            JToken moduleArg = args != null ? args["module"] : null;
            int habId = OptionalInt(args, "hab");
            if (moduleArg == null || moduleArg.Type == JTokenType.Null)
            {
                // A hab with no module names the modules it holds: a module state
                // id is not addressable any other way (slot is an index within a
                // sector, so it repeats across a hab), and this listing is the
                // discovery path.
                if (habId >= 0)
                    throw new VerbError("arg 'module' is a module state id; "
                        + ModuleList(ById<TIHabState>(habId)));
                throw new VerbError("missing arg 'module' (a TIHabModuleState id); "
                    + "pass 'hab' alone to list a hab's modules and their ids");
            }

            TIHabModuleState module = Arg<TIHabModuleState>(args, "module");
            TIHabState hab = Safe<TIHabState>(delegate { return module.hab; }, null);
            if (hab == null)
                throw new VerbError("module " + (int)module.ID + " has no hab");
            if (habId >= 0 && habId != (int)hab.ID)
                throw new VerbError("module " + (int)module.ID + " belongs to hab "
                    + (int)hab.ID + " (" + StateName(hab) + "), not " + habId);

            // Refused rather than passed through: DestroyModule skips its whole
            // destruction branch for a module that is not okay, then still runs
            // the full-destruction check, so calling it on an already-dead module
            // of a hab whose last module is gone would destroy the HAB and report
            // it as this call's doing.
            if (!Safe<bool>(delegate { return module.okay; }, false))
                throw new VerbError("module " + (int)module.ID + " is not okay ("
                    + ModuleCondition(module) + "), and destroying it is a no-op "
                    + "that can still take the hab down; " + ModuleList(hab));

            TIFactionState destroyer = null;
            int destroyerId = OptionalInt(args, "destroyer");
            if (destroyerId >= 0) destroyer = ById<TIFactionState>(destroyerId);
            // A switch, not an amount. DestroyModule only ever tests `hate > 0`;
            // what it then applies is the module's tier times the global config's
            // factionHateMultiplierPerModuleDestroyedPerTier, and the number
            // passed in is never read again. Any positive value means the same
            // thing, so the response reports what the engine will actually apply
            // rather than echoing the input as though it were the amount.
            float hate = Float(args, "hate", 0f);
            if (hate < 0f) throw new VerbError("arg 'hate' must be >= 0");
            bool hateOn = hate > 0f;
            if (hateOn && destroyer == null)
                throw new VerbError("arg 'hate' needs a 'destroyer': the engine "
                    + "only applies hate and atrocities when a destroying faction "
                    + "is named");

            bool core = Safe<bool>(delegate
            {
                TIHabModuleTemplate t = module.moduleTemplate;
                return t != null && t.coreModule;
            }, false);

            var o = new JObject();
            o["hab"] = Describe(hab);
            o["module"] = DescribeModule(module);
            o["coreModule"] = core;
            o["destroyer"] = destroyer != null ? Describe(destroyer) : (JToken)JValue.CreateNull();
            o["hate"] = HateToken(module, hateOn, hate);
            int okayBefore = ModuleCount(hab, true);
            o["okayModulesBefore"] = okayBefore;

            // The engine's own destruction, the same call TIEffectsState's
            // DestroyRandomModules makes with every optional argument left at its
            // default: log it, count it toward the hab's destruction, alert, no
            // hate unless asked, repower the hab afterwards. Decommissioning is
            // the wrong path here -- CompleteDecommissionModule empties the slot
            // and refunds the build cost, which models a teardown, while this
            // leaves wreckage in the slot, fires HabModuleDestroyed, and takes the
            // hab down when the last okay module goes.
            bool destroyed = hab.DestroyModule(destroyer, module, false, false,
                true, hate, false, false);

            o["destroyed"] = destroyed;
            o["moduleAfter"] = Safe<JToken>(
                delegate { return DescribeModule(module); }, JValue.CreateNull());
            bool habGone = Safe<bool>(delegate { return hab.archived; }, false);
            o["habDestroyed"] = habGone;
            o["okayModulesAfter"] = habGone ? -1 : ModuleCount(hab, true);
            o["presentModulesAfter"] = habGone ? -1 : ModuleCount(hab, false);

            var warnings = new List<string>();
            if (!destroyed)
                warnings.Add("the engine refused the destruction and reported "
                    + "false; the one case that does this is a core module, or one "
                    + "whose template carries HabModuleSpecialRule.AlienWormhole, "
                    + "on the alien faction's primary hab");
            if (core && destroyed)
                warnings.Add("a core module was destroyed; vanilla bombardment "
                    + "and combat never pick the core, so this is a hab state no "
                    + "play path produces");
            if (habGone)
                warnings.Add("that was the hab's last okay module, so the engine "
                    + "destroyed the hab itself");
            if (hateOn)
                warnings.Add("hate was switched on, so the hab's owner gained "
                    + "hate toward the destroyer and both sides may have "
                    + "committed atrocities; see the hate object for the amount "
                    + "the engine computed");
            if (warnings.Count > 0)
                o["warning"] = string.Join("; ", warnings.ToArray());
            return o;
        }

        // What the engine will actually apply, read the same way DestroyModule
        // computes it: tier * factionHateMultiplierPerModuleDestroyedPerTier,
        // with the requested number never entering the arithmetic. Read before
        // the destruction, because the module's tier goes with its template.
        static JObject HateToken(TIHabModuleState module, bool enabled, float requested)
        {
            var o = new JObject();
            o["requested"] = Num(requested);
            o["enabled"] = enabled;
            Put(o, "tier", delegate { return (JToken)module.tier; });
            Put(o, "perTierMultiplier", delegate
            {
                return Num(TemplateManager.global
                    .factionHateMultiplierPerModuleDestroyedPerTier);
            });
            o["applied"] = !enabled ? Num(0f) : Safe<JToken>(delegate
            {
                return Num(module.tier * TemplateManager.global
                    .factionHateMultiplierPerModuleDestroyedPerTier);
            }, JValue.CreateNull());
            return o;
        }

        static int ModuleCount(TIHabState hab, bool okayOnly)
        {
            return Safe<int>(delegate
            {
                List<TIHabModuleState> list = okayOnly
                    ? hab.OkayModules() : hab.PresentModules();
                return list != null ? list.Count : 0;
            }, -1);
        }

        static string ModuleList(TIHabState hab)
        {
            List<TIHabModuleState> modules = Safe<List<TIHabModuleState>>(
                delegate { return hab.PresentModules(); }, null);
            if (modules == null || modules.Count == 0)
                return StateName(hab) + " (" + (int)hab.ID + ") holds no modules";
            var rows = new List<string>();
            int n = modules.Count < MaxModulesListed ? modules.Count : MaxModulesListed;
            for (int i = 0; i < n; i++)
            {
                TIHabModuleState m = modules[i];
                if (m == null) continue;
                rows.Add((int)m.ID + "=" + ModuleTemplateName(m)
                    + " [sector " + Safe<int>(delegate { return m.sector.sectorNum; }, -1)
                    + " slot " + Safe<int>(delegate { return m.slot; }, -1)
                    + ", " + ModuleCondition(m) + "]");
            }
            return StateName(hab) + " (" + (int)hab.ID + ") holds "
                + modules.Count + " modules: " + string.Join(", ", rows.ToArray())
                + (modules.Count > n ? ", ..." : "");
        }

        static string ModuleTemplateName(TIHabModuleState module)
        {
            return Safe<string>(delegate
            {
                TIHabModuleTemplate t = module.moduleTemplate;
                return t != null ? t.dataName : "<empty>";
            }, "<unreadable>");
        }

        static string ModuleCondition(TIHabModuleState module)
        {
            if (Safe<bool>(delegate { return module.empty; }, false)) return "empty";
            if (Safe<bool>(delegate { return module.destroyed; }, false)) return "destroyed";
            if (Safe<bool>(delegate { return module.decommissioning; }, false))
                return "decommissioning";
            if (Safe<bool>(delegate { return module.underConstruction; }, false))
                return "okay, under construction";
            return "okay";
        }

        #endregion
    }
}

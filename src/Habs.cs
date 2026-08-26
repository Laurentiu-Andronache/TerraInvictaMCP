using System;
using System.Collections.Generic;
using System.Globalization;
using Newtonsoft.Json.Linq;
using PavonisInteractive.TerraInvicta;

namespace TerraInvictaMCP
{
    // module.power.
    //
    // The play path's own switch. HabitatsScreenController.OnModulePowerToggle
    // builds Actions.UpdateHabModulePowerStatus, whose Execute body is
    // GetState<TIHabModuleState>(moduleID).SetPowerStatus(status, false) plus a
    // screen refresh, so calling SetPowerStatus directly is that same write with
    // no UI attached. skipFullResourceUpdate stays false, the value the UI
    // passes: true is what the engine's batch power manager uses before doing one
    // recalculation at the end, and a single flip made with it leaves the hab's
    // resource-income and mission-control caches stale.
    //
    // Neither UpdatePowerManagement nor HabPowerManagementUpdated is touched.
    // The UI path fires neither, and UpdatePowerManagement(turnEverythingPossibleOn:
    // true) would turn a module this verb just switched off straight back on.
    //
    // SetPowerStatus never reports. It returns void, throws nothing, and coerces
    // in silence: it returns at once when the module already holds the requested
    // state or has no template; it forces powered false while the module is
    // decommissioning or while the hab has no completed core; and when asked to
    // turn one ON it forces false for a consumer drawing more than the hab's net
    // power, then for a module failing its population requirement. It checks
    // neither CanTurnOff, nor destroyed, nor underConstruction, nor who owns the
    // hab, so it will depower a core module or another faction's. That is why
    // this verb gates on the screen's own preconditions first and refuses with
    // the condition that failed, then reads `powered` back rather than assuming
    // the write landed.
    public static partial class Verbs
    {
        #region module.power

        static JToken ModuleSetPower(JObject args)
        {
            JToken moduleArg = args != null ? args["module"] : null;
            int habId = OptionalInt(args, "hab");
            if (moduleArg == null || moduleArg.Type == JTokenType.Null)
            {
                // Same discovery path as kill.module, through the same listing: a
                // module state id is not addressable any other way, since `slot`
                // is an index within a sector and repeats across a hab.
                if (habId >= 0)
                    throw new VerbError("arg 'module' is a module state id; "
                        + ModuleList(ById<TIHabState>(habId)));
                throw new VerbError("missing arg 'module' (a TIHabModuleState id); "
                    + "pass 'hab' alone to list a hab's modules and their ids");
            }
            bool on = RequiredSwitch(args, "on");

            TIHabModuleState module = Arg<TIHabModuleState>(args, "module");
            TIHabState hab = Safe<TIHabState>(delegate { return module.hab; }, null);
            if (hab == null)
                throw new VerbError("module " + (int)module.ID + " has no hab");
            if (habId >= 0 && habId != (int)hab.ID)
                throw new VerbError("module " + (int)module.ID + " belongs to hab "
                    + (int)hab.ID + " (" + StateName(hab) + "), not " + habId);

            bool before = Safe<bool>(delegate { return module.powered; }, false);

            // The engine's own first line is `if (powerSetting == powered ||
            // moduleTemplate == null) return;`, so a request for the state the
            // module already holds writes nothing, and the call is skipped
            // rather than made. The structural gates still run on it -- asking
            // to power a core module should say so whichever way the flag
            // happens to sit -- while the coercion predictors do not: the
            // hab's completed-core check, CanPower and CanDepower all read the
            // hab as it stands now and would refuse a write that was never
            // going to happen, by a coercion the engine returns before
            // reaching.
            RefusePowerChange(module, hab, on, before != on);
            if (before == on) return PowerResult(hab, module, on, before, before);

            module.SetPowerStatus(on, false);
            bool after = Safe<bool>(delegate { return module.powered; }, before);
            return PowerResult(hab, module, on, before, after);
        }

        static JToken PowerResult(TIHabState hab, TIHabModuleState module,
            bool requested, bool before, bool after)
        {
            var o = new JObject();
            o["hab"] = Describe(hab);
            o["module"] = DescribeModule(module);
            o["requested"] = requested;
            o["poweredBefore"] = before;
            o["powered"] = after;
            o["changed"] = after != before;
            // The read-back, never the request. SetPowerStatus reports nothing at
            // all, so `powered` afterwards is the only evidence the flip took.
            o["applied"] = after == requested;
            o["habPower"] = HabPower(hab);
            if (after != requested)
                o["warning"] = "the engine did not take the setting: it was asked "
                    + "to power the module " + (requested ? "on" : "off")
                    + " and `powered` reads " + (after ? "true" : "false")
                    + " afterwards. SetPowerStatus coerces silently, so re-read "
                    + "the module's canPower/canDepower and the hab's netPower "
                    + "for the condition that moved underneath the gates";
            return o;
        }

        // The two numbers CanPower and CanDepower both compare against, plus the
        // module counts around them. Every read is one pass over the hab's own
        // module list, which is bounded by its sectors' slots.
        static JObject HabPower(TIHabState hab)
        {
            var o = new JObject();
            // The same arguments the engine's own power checks pass: neither
            // under-construction nor deactivated modules count toward it.
            Put(o, "netPower", delegate
            {
                return (JToken)hab.NetPower(false, false);
            });
            Put(o, "anyCoreCompleted", delegate
            {
                return (JToken)hab.anyCoreCompleted;
            });
            Put(o, "activeModules", delegate { return Count(hab.ActiveModules()); });
            Put(o, "unpoweredModules", delegate { return Count(hab.UnpoweredModules()); });
            Put(o, "presentModules", delegate { return Count(hab.PresentModules()); });
            return o;
        }

        static JToken Count(List<TIHabModuleState> list)
        {
            return (JToken)(list != null ? list.Count : 0);
        }

        // The Habitats screen's own preconditions, each refused by name. The
        // engine enforces none of them on this path, and every one of them is a
        // silent coercion rather than a failure, so a call made past one would
        // answer with a state the caller never asked for.
        //
        // CanPower and CanDepower are called rather than reimplemented: they are
        // public, they are what the screen tests, and their clauses would drift
        // out from under a copy. What the refusals add is the conditions as read,
        // so the caller sees which one moved. Those two and the completed-core
        // check are the coercion predictors a no-op skips, which is what
        // `changing` carries.
        static void RefusePowerChange(TIHabModuleState module, TIHabState hab,
            bool on, bool changing)
        {
            int id = (int)module.ID;
            string name = ModuleTemplateName(module);
            TIHabModuleTemplate template = Safe<TIHabModuleTemplate>(
                delegate { return module.moduleTemplate; }, null);

            if (template == null)
                throw new VerbError("module " + id + " has no template ("
                    + ModuleCondition(module) + "); SetPowerStatus returns without "
                    + "writing anything for one");
            if (Safe<bool>(delegate { return module.empty; }, false)
                || Safe<bool>(delegate { return module.destroyed; }, false))
                throw new VerbError("module " + id + " (" + name + ") is "
                    + ModuleCondition(module) + ", and a slot in that condition has "
                    + "no power switch; the engine checks neither and would take a "
                    + "depower request on it");
            if (!Safe<bool>(delegate { return template.CanTurnOff; }, false))
                throw new VerbError("module " + id + " (" + name + ") is a core "
                    + "module: CanTurnOff is exactly !coreModule, and the Habitats "
                    + "screen offers no toggle for one. The engine checks it "
                    + "nowhere, so this is the only thing standing between the call "
                    + "and an unpowered core");
            if (Safe<bool>(delegate { return module.underConstruction; }, false))
                throw new VerbError("module " + id + " (" + name + ") is still "
                    + "under construction; the screen's toggle wants a finished "
                    + "module");
            if (Safe<bool>(delegate { return module.decommissioning; }, false))
                throw new VerbError("module " + id + " (" + name + ") is "
                    + "decommissioning, and SetPowerStatus forces one unpowered "
                    + "whatever it is asked for");

            if (!changing) return;
            // A coercion predictor, so it sits with the other two: the engine
            // reads anyCoreCompleted only after its own `powerSetting ==
            // powered` return, and a no-op never reaches the coercion.
            if (on && !Safe<bool>(delegate { return hab.anyCoreCompleted; }, false))
                throw new VerbError("hab " + (int)hab.ID + " (" + StateName(hab)
                    + ") has no completed core module, and SetPowerStatus forces "
                    + "every module of such a hab unpowered whatever it is asked "
                    + "for");
            if (on && !Safe<bool>(delegate { return module.CanPower(); }, false))
                throw new VerbError("CanPower() is false for module " + id + " ("
                    + name + "), which is the screen's own gate on powering one up; "
                    + "conditions as read: " + PowerConditions(module, hab));
            if (!on && !Safe<bool>(delegate { return module.CanDepower(); }, false))
                throw new VerbError("CanDepower() is false for module " + id + " ("
                    + name + "), which is the screen's own gate on shutting one "
                    + "down. The engine refuses to pull a generator the hab still "
                    + "needs, a shipyard module with a queued build or a fleet "
                    + "repairing at it, a resupply module with a fleet resupplying, "
                    + "and a PowerFirst module unless the hab is already running a "
                    + "power deficit with no unpowered generator left to switch on "
                    + "instead; conditions as read: " + DepowerConditions(module, hab));
        }

        // Each of CanPower's own clauses, read rather than re-decided.
        static string PowerConditions(TIHabModuleState module, TIHabState hab)
        {
            var parts = new List<string>();
            parts.Add("decommissioning=" + Read(delegate { return module.decommissioning; }));
            parts.Add("destroyed=" + Read(delegate { return module.destroyed; }));
            parts.Add("meetsPopulationRequirements="
                + Read(delegate { return module.MeetsPopulationRequirements(); }));
            parts.Add("powerConsumer=" + Read(delegate { return module.PowerConsumer(); }));
            parts.Add("powerConsumed=" + Read(delegate { return module.PowerConsumed(); }));
            parts.Add("habNetPower=" + Read(delegate { return hab.NetPower(false, false); }));
            return string.Join(", ", parts.ToArray());
        }

        // The numbers behind CanDepower's two power clauses. `powerProvider`
        // says which one can fire: true is the generator clause, refusing when
        // habNetPower < modulePower and returning before anything else runs,
        // and false falls through to the shipyard, resupply and PowerFirst
        // clauses. The last refuses whenever powerFirst is true unless
        // habNetPower < 0 and habUnpoweredProviders is 0, which is the
        // refusal a healthy hab hits and the reason both are reported.
        // The shipyard queue and what the docked fleets are doing stay in the
        // message's prose: reproducing those scans here is exactly the
        // duplication that goes stale.
        static string DepowerConditions(TIHabModuleState module, TIHabState hab)
        {
            var parts = new List<string>();
            parts.Add("modulePower=" + Read(delegate { return module.ModulePower(); }));
            parts.Add("powerProvider=" + Read(delegate { return module.PowerProvider(); }));
            // Exactly CanDepower's own test: SpecialRules is specialRules with
            // the None entries filtered out, and PowerFirst is not None.
            parts.Add("powerFirst=" + Read(delegate
            {
                TIHabModuleTemplate template = module.moduleTemplate;
                return template != null ? (object)template.PowerFirst : null;
            }));
            parts.Add("habUnpoweredProviders="
                + Read(delegate { return UnpoweredProviders(hab); }));
            parts.Add("habNetPower=" + Read(delegate { return hab.NetPower(false, false); }));
            parts.Add("habDockedFleets=" + Read(delegate
            {
                List<TISpaceFleetState> docked = hab.dockedFleets;
                return docked != null ? docked.Count : 0;
            }));
            return string.Join(", ", parts.ToArray());
        }

        // The idle generators CanDepower's PowerFirst clause looks for: it
        // holds only while UnpoweredModules() has None that PowerProvider().
        static int UnpoweredProviders(TIHabState hab)
        {
            List<TIHabModuleState> unpowered = hab.UnpoweredModules();
            if (unpowered == null) return 0;
            int count = 0;
            for (int i = 0; i < unpowered.Count; i++)
            {
                TIHabModuleState m = unpowered[i];
                if (m != null && m.PowerProvider()) count++;
            }
            return count;
        }

        // One condition, rendered for a refusal message. Boxed so a throwing read
        // and a null read stay distinguishable, and bools come out in the JSON
        // spelling rather than .NET's "True".
        static string Read<T>(Func<T> read)
        {
            object value;
            try { value = read(); }
            catch (Exception) { return "<unreadable>"; }
            if (value == null) return "null";
            if (value is bool) return ((bool)value) ? "true" : "false";
            var formattable = value as IFormattable;
            return formattable != null
                ? formattable.ToString(null, CultureInfo.InvariantCulture)
                : value.ToString();
        }

        // A required switch, read strictly. Bool()/Flag() answer false for
        // anything that is not the word "true", so a typo'd value would arrive
        // here as a depower request the caller never made.
        static bool RequiredSwitch(JObject args, string key)
        {
            JToken t = args != null ? args[key] : null;
            if (t == null || t.Type == JTokenType.Null)
                throw new VerbError("missing arg '" + key + "': true powers the "
                    + "module on, false shuts it down");
            if (t.Type == JTokenType.Boolean) return (bool)t;
            string s = t.ToString();
            if (string.Equals(s, "true", StringComparison.OrdinalIgnoreCase)) return true;
            if (string.Equals(s, "false", StringComparison.OrdinalIgnoreCase)) return false;
            throw new VerbError("arg '" + key + "' must be true or false, not '"
                + s + "'");
        }

        #endregion
    }
}

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
                        + ModuleListFor(habId));
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
        // anything they do not recognize as true -- they take the booleans, a
        // nonzero number, and "true", "1" or "yes" -- so a typo'd value would
        // arrive here as a depower request the caller never made.
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

        #region hab.build_module

        // The Habitats screen's own build, with no screen open.
        //
        // HabitatsScreenController.SetupModuleBuild hands
        // activePlayer.playerControl a BuildHabModuleAction(template, sector,
        // slot, cost, callback), and Entities.Player.StartAction is a bare
        // action.Execute(), so the action runs inside this frame. That Execute
        // body is exactly hab.InitiateModuleConstruction(sector, slot,
        // template, cost), which depowers whatever stands in the slot, unhooks
        // its shipyard, starts the build, PAYS the cost, records the
        // expenditure, rewires the connectors, reruns power management and
        // checks the faction's objectives.
        //
        // spawn.module reaches none of that. It calls
        // TIHabModuleState.InitiateConstructModule directly and completes it,
        // so no faction is ever billed, no prereq is ever read and no upgrade
        // decision is ever made. That is the difference between the two verbs,
        // and it is why an upgrade chain can only be exercised through this one.
        //
        // The upgrade-versus-new-build decision is the engine's, read exactly
        // the way TIHabState.ApplySavedTemplate's own TryInstallModule reads it:
        //
        //   free      = slot.empty || slot.destroyed || slot.decommissioning
        //   isUpgrade = slot.CanUpgrade(faction)
        //               && template.UpgradesFrom == slot.moduleTemplate
        //
        // and it proceeds when either holds. `isUpgrade` is then handed to the
        // cost call, which is what makes an upgrade cost less than a fresh
        // module, so getting it wrong would silently overcharge or undercharge.
        // Both flags are reported: which of the two a call took is the thing a
        // test of an upgrade chain is actually asking.

        // What one slot would be, for this template, if the build went there.
        class BuildSlot
        {
            public TIHabModuleState module;
            public TISectorState sector;
            public int index;
            public bool free;
            public bool isUpgrade;
            public bool validForSlot;
            public string chosenBy;

            public bool Eligible { get { return validForSlot && (free || isUpgrade); } }
        }

        static JToken HabBuildModule(JObject args)
        {
            TIHabState hab = Arg<TIHabState>(args, "hab");
            string name = Str(args, "module");
            if (string.IsNullOrEmpty(name))
                throw new VerbError("missing arg 'module' (a TIHabModuleTemplate "
                    + "data name)");
            TIHabModuleTemplate template = ModuleTemplate(name);

            TIFactionState faction = Safe<TIFactionState>(
                delegate { return hab.faction; }, null);
            if (faction == null)
                throw new VerbError("hab " + (int)hab.ID + " (" + StateName(hab)
                    + ") has no owning faction, and every step of this path takes "
                    + "one: the allowed-module gate, the cost, and the payment");

            // The hab-level gate, and the same one CanUpgrade tests. It answers
            // false for EVERY module while the hab is decommissioning, under
            // bombardment or under assault, as well as for one this faction has
            // not unlocked or this hab cannot hold.
            if (!Safe<bool>(delegate {
                    return hab.IsModuleAllowedForThisHab(faction, template, false);
                }, false))
                throw new VerbError("hab " + (int)hab.ID + " (" + StateName(hab)
                    + ") does not allow " + template.dataName + ": "
                    + "IsModuleAllowedForThisHab is false for it. That covers a "
                    + "module the owner has not unlocked, one this hab cannot "
                    + "hold, and every module at all while the hab is "
                    + "decommissioning, under bombardment or under assault. "
                    + "Allowed here right now: " + AllowedList(hab, faction)
                    + ". spawn.module installs one regardless -- it is the "
                    + "fixture, and it bypasses this gate");

            int targetId = OptionalInt(args, "target");
            BuildSlot plan;
            if (targetId >= 0)
            {
                TIHabModuleState slot = ById<TIHabModuleState>(targetId);
                TIHabState owner = Safe<TIHabState>(delegate { return slot.hab; }, null);
                if (owner == null || (int)owner.ID != (int)hab.ID)
                    throw new VerbError("slot " + targetId + " belongs to "
                        + (owner != null ? "hab " + (int)owner.ID + " ("
                            + StateName(owner) + ")" : "no hab")
                        + ", not to hab " + (int)hab.ID);
                plan = Evaluate(hab, faction, template, slot);
                plan.chosenBy = "the target argument";
                if (!plan.Eligible)
                    throw new VerbError("slot " + targetId + " will not take "
                        + template.dataName + ": " + Why(plan, template) + ". "
                        + SlotList(hab, faction, template));
            }
            else
            {
                plan = PickSlot(hab, faction, template);
                if (plan == null)
                    throw new VerbError("no slot in hab " + (int)hab.ID + " ("
                        + StateName(hab) + ") will take " + template.dataName
                        + ": none is free (empty, destroyed or decommissioning) "
                        + "and none holds a module this one upgrades from. "
                        + SlotList(hab, faction, template));
            }

            string costSource;
            TIResourcesCost cost = BuildModuleCost(
                hab, faction, template, plan.isUpgrade, out costSource);
            if (cost == null)
                throw new VerbError("the engine returned no cost for "
                    + template.dataName + " at hab " + (int)hab.ID
                    + ", so there is nothing to pay and nothing to record; "
                    + "the build was not started");
            bool affordable = Affordable(cost, faction);

            var o = new JObject();
            o["hab"] = Describe(hab);
            o["template"] = template.dataName;
            o["decision"] = Decision(plan, template);
            o["cost"] = CostToken(cost);
            o["costFrom"] = costSource;
            o["affordable"] = affordable;

            if (!affordable)
            {
                o["queued"] = false;
                o["error"] = "the owner cannot afford it. The Habitats screen "
                    + "would not offer the build either: TryInstallModule tests "
                    + "CanAfford(faction, 1, null, infinity) before it submits, "
                    + "and this verb makes the same test rather than charging a "
                    + "faction into deficit. give_resources funds it, or "
                    + "spawn.module installs one for free";
                o["resources"] = Resources(faction);
                // The payload is worth having on a refusal, so this answers
                // rather than throwing; `queued` false is the failure.
                return o;
            }

            // The play path's own submission. Player.StartAction is
            // `action.Execute()` and nothing else, so a faction with no
            // playerControl (nothing guarantees one) runs the same body
            // directly rather than being refused for a field the engine only
            // uses as a call forwarder.
            var action = new PavonisInteractive.TerraInvicta.Actions.BuildHabModuleAction(
                template, plan.sector, plan.index, cost, null);
            PavonisInteractive.TerraInvicta.Entities.Player control =
                Safe<PavonisInteractive.TerraInvicta.Entities.Player>(
                    delegate { return faction.playerControl; }, null);
            if (control != null) control.StartAction(action);
            else action.Execute();

            o["queued"] = true;
            o["submittedVia"] = control != null
                ? "playerControl.StartAction(BuildHabModuleAction)"
                : "BuildHabModuleAction.Execute() (the faction has no "
                  + "playerControl; StartAction is that call and nothing else)";
            // Read back rather than asserted. InitiateModuleConstruction returns
            // void and can leave early -- it plays a refusal sound and returns
            // when the sector is null, has no faction, or holds fewer modules
            // than the slot index.
            o["module"] = DescribeModule(plan.module);
            o["started"] = Safe<bool>(
                delegate { return plan.module.underConstruction; }, false);
            o["resources"] = Resources(faction);
            return o;
        }

        // Every slot of the hab, with the two flags the decision reads. Bounded
        // by the hab's own slot count, which is at most a few dozen.
        static BuildSlot Evaluate(TIHabState hab, TIFactionState faction,
            TIHabModuleTemplate template, TIHabModuleState slot)
        {
            var plan = new BuildSlot();
            plan.module = slot;
            plan.sector = Safe<TISectorState>(delegate { return slot.sector; }, null);
            plan.index = Safe<int>(delegate { return slot.slot; }, -1);
            plan.free = Safe<bool>(delegate { return slot.empty; }, false)
                || Safe<bool>(delegate { return slot.destroyed; }, false)
                || Safe<bool>(delegate { return slot.decommissioning; }, false);
            // Reference equality, because that is the comparison the engine
            // makes (bne.un on the two template objects). UpgradesFrom caches
            // the template it resolves, so the same object comes back every
            // time and a dataName comparison would only differ where the
            // engine's own decision differs -- which would put the cost this
            // verb computes out of step with the build it submits.
            plan.isUpgrade = Safe<bool>(delegate
            {
                return slot.CanUpgrade(faction)
                    && ReferenceEquals(template.UpgradesFrom, slot.moduleTemplate);
            }, false);
            plan.validForSlot = plan.sector != null && plan.index >= 0
                && Safe<bool>(delegate {
                    return plan.sector.ValidModuleForSlot(template, plan.index); }, false);
            return plan;
        }

        // The first slot that will take this module, upgrades first. An upgrade
        // is preferred over an empty slot on purpose: it is the decision this
        // verb exists to exercise, and picking the empty slot instead would
        // quietly build a second module beside the one the caller meant to
        // upgrade.
        static BuildSlot PickSlot(TIHabState hab, TIFactionState faction,
            TIHabModuleTemplate template)
        {
            BuildSlot free = null;
            List<TISectorState> sectors = Safe<List<TISectorState>>(
                delegate { return hab.activeSectors; }, null);
            for (int s = 0; sectors != null && s < sectors.Count; s++)
            {
                TISectorState sector = sectors[s];
                List<TIHabModuleState> slots = sector != null
                    ? Safe<List<TIHabModuleState>>(
                        delegate { return sector.habModules; }, null)
                    : null;
                for (int i = 0; slots != null && i < slots.Count; i++)
                {
                    if (slots[i] == null) continue;
                    BuildSlot plan = Evaluate(hab, faction, template, slots[i]);
                    if (!plan.Eligible) continue;
                    if (plan.isUpgrade)
                    {
                        plan.chosenBy = "the first slot holding a module this "
                            + "one upgrades from";
                        return plan;
                    }
                    if (free == null)
                    {
                        plan.chosenBy = "the first free slot; no slot holds a "
                            + "module this one upgrades from";
                        free = plan;
                    }
                }
            }
            return free;
        }

        static JObject Decision(BuildSlot plan, TIHabModuleTemplate template)
        {
            var o = new JObject();
            o["kind"] = plan.isUpgrade ? "upgrade" : "newBuild";
            o["chosenBy"] = plan.chosenBy;
            o["slot"] = plan.module != null
                ? DescribeModule(plan.module) : (JToken)JValue.CreateNull();
            o["slotFree"] = plan.free;
            o["isUpgrade"] = plan.isUpgrade;
            o["validForSlot"] = plan.validForSlot;
            Put(o, "replaces", delegate
            {
                TIHabModuleTemplate standing = plan.module.moduleTemplate;
                return standing != null
                    ? (JToken)new JValue(standing.dataName) : JValue.CreateNull();
            });
            Put(o, "upgradesFrom", delegate
            {
                TIHabModuleTemplate from = template.UpgradesFrom;
                return from != null
                    ? (JToken)new JValue(from.dataName) : JValue.CreateNull();
            });
            return o;
        }

        static string Why(BuildSlot plan, TIHabModuleTemplate template)
        {
            if (!plan.validForSlot)
                return "TISectorState.ValidModuleForSlot is false for it "
                    + "(sector " + Safe<int>(delegate {
                        return plan.sector.sectorNum; }, -1)
                    + " slot " + plan.index + "), which is the per-slot rule the "
                    + "Habitats screen builds its own module list from";
            return "it is neither free (empty, destroyed or decommissioning) nor "
                + "an upgrade: it holds " + ModuleTemplateName(plan.module)
                + " and " + template.dataName + " upgrades from "
                + Safe<string>(delegate
                {
                    TIHabModuleTemplate from = template.UpgradesFrom;
                    return from != null ? from.dataName : "nothing";
                }, "<unreadable>");
        }

        // The price the play path pays, computed the way TryInstallModule
        // computes it. A faction with space resources unlocked and some in hand
        // buys from space, falling back to the boost-substituted price when the
        // plain one is out of reach; everything else buys from Earth. isUpgrade
        // rides into every one of them, which is the discount.
        static TIResourcesCost BuildModuleCost(TIHabState hab, TIFactionState faction,
            TIHabModuleTemplate template, bool isUpgrade, out string source)
        {
            bool fromSpace = Safe<bool>(delegate
            {
                return faction.UnlockedSpaceResources && faction.HasAnySpaceResources;
            }, false);
            if (!fromSpace)
            {
                source = "earth";
                return Safe<TIResourcesCost>(delegate {
                    return template.CostFromEarth(faction, hab, isUpgrade); }, null);
            }
            TIResourcesCost plain = Safe<TIResourcesCost>(delegate
            {
                // The copy is the engine's: it tests affordability on a copy and
                // pays with it.
                return new TIResourcesCost(
                    template.CostFromSpace(faction, hab, isUpgrade, false, 0, false));
            }, null);
            if (plain != null && Affordable(plain, faction))
            {
                source = "space";
                return plain;
            }
            source = "space, with boost substitution";
            TIResourcesCost substituted = Safe<TIResourcesCost>(delegate {
                return template.CostFromSpace(faction, hab, isUpgrade, true, 0, false);
            }, null);
            if (substituted != null) return substituted;
            source = "space";
            return plain;
        }

        // The engine's own affordability arguments, from TryInstallModule:
        // the whole treasury may be spent and no resource is preserved.
        static bool Affordable(TIResourcesCost cost, TIFactionState faction)
        {
            return Safe<bool>(delegate
            {
                return cost.CanAfford(faction, 1f, null, float.PositiveInfinity);
            }, false);
        }

        static string AllowedList(TIHabState hab, TIFactionState faction)
        {
            List<TIHabModuleTemplate> allowed = Safe<List<TIHabModuleTemplate>>(
                delegate { return hab.AllowedModules(faction); }, null);
            if (allowed == null) return "<unreadable>";
            if (allowed.Count == 0) return "nothing";
            var names = new List<string>();
            int n = allowed.Count < MaxModulesListed ? allowed.Count : MaxModulesListed;
            for (int i = 0; i < n; i++)
            {
                if (allowed[i] != null) names.Add(allowed[i].dataName);
            }
            return string.Join(", ", names.ToArray())
                + (allowed.Count > n ? ", ..." : "");
        }

        // Every slot with its id and its verdict for THIS template, which is what
        // a caller needs to pick a target after a refusal. PresentModules (what
        // the kill.module listing uses) skips the empty slots, which are most of
        // the answer here.
        static string SlotList(TIHabState hab, TIFactionState faction,
            TIHabModuleTemplate template)
        {
            var rows = new List<string>();
            List<TISectorState> sectors = Safe<List<TISectorState>>(
                delegate { return hab.activeSectors; }, null);
            for (int s = 0; sectors != null && s < sectors.Count; s++)
            {
                TISectorState sector = sectors[s];
                List<TIHabModuleState> slots = sector != null
                    ? Safe<List<TIHabModuleState>>(
                        delegate { return sector.habModules; }, null)
                    : null;
                for (int i = 0; slots != null && i < slots.Count; i++)
                {
                    if (slots[i] == null || rows.Count >= MaxModulesListed) continue;
                    BuildSlot plan = Evaluate(hab, faction, template, slots[i]);
                    rows.Add((int)slots[i].ID + "=" + ModuleTemplateName(slots[i])
                        + " [sector " + Safe<int>(delegate {
                            return sector.sectorNum; }, -1)
                        + " slot " + plan.index + ", " + ModuleCondition(slots[i])
                        + ", " + (plan.Eligible
                            ? (plan.isUpgrade ? "upgradeable" : "free")
                            : "not a target") + "]");
                }
            }
            if (rows.Count == 0) return "the hab reports no slots";
            return "slots in " + StateName(hab) + " (" + (int)hab.ID + "): "
                + string.Join(", ", rows.ToArray());
        }

        #endregion
    }
}

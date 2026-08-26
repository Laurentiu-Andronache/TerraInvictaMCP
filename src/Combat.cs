using System;
using System.Collections.Generic;
using System.Globalization;
using Newtonsoft.Json.Linq;
using PavonisInteractive.TerraInvicta;
using PavonisInteractive.TerraInvicta.SpaceCombat.UI;

namespace TerraInvictaMCP
{
    // spawn.fleet, combat.start, combat.status, combat.autoresolve, fleet.bombard.
    public static partial class Verbs
    {
        const int MaxSpawnShips = 20;

        // An armed autoresolve that stops making progress disarms rather than sitting
        // forever; a minute of frames is far longer than any observed simulation.
        const int AutoresolveFrameBudget = 3600;

        static JToken SpawnFleet(JObject args)
        {
            TIFactionState faction = Arg<TIFactionState>(args, "faction");
            TIGameState location = Location(Int(args, "location"));
            int count = Int(args, "ships");
            if (count < 1 || count > MaxSpawnShips)
                throw new VerbError("arg 'ships' must be 1.." + MaxSpawnShips);

            TISpaceShipTemplate design = Design(faction, Str(args, "design"));
            // CompleteShipInitialization dereferences designingFaction, which resolves
            // lazily from the template's factionName and is null when that name is not a
            // faction. Settle it before anything is created.
            TIFactionState designer = design.designingFaction;
            if (designer == null)
                throw new VerbError("design '" + design.dataName + "' has no designing faction");

            // Every ship is registered in GameStateManager the moment it is created, so a
            // failure part way through the batch would leave the rest behind to ride into
            // the next save.
            var ships = new List<TISpaceShipState>();
            try
            {
                for (int i = 0; i < count; i++)
                {
                    var ship = design.CreateGameState() as TISpaceShipState;
                    if (ship == null) throw new VerbError("design did not create a ship state");
                    ships.Add(ship);
                    ship.InitWithTemplate(design);
                    ship.CompleteShipInitialization();
                    string name = null;
                    try { name = TISpaceAssetState.GetRandomAssetName(ship, faction); }
                    catch (Exception) { }
                    ship.SetDisplayName(!string.IsNullOrEmpty(name) ? name : design.displayName);
                }
            }
            catch (Exception)
            {
                for (int i = 0; i < ships.Count; i++) Discard(ships[i]);
                throw;
            }

            TISpaceFleetState fleet = TISpaceFleetState.CreateAtRunTime(
                faction, ships, location, null, null, false, false, null);
            if (fleet == null) throw new VerbError("fleet creation returned null");

            var o = new JObject();
            o["id"] = (int)fleet.ID;
            o["name"] = StateName(fleet);
            o["faction"] = Describe(faction);
            o["location"] = Describe(location);
            o["design"] = DescribeDesign(design);
            var a = new JArray();
            for (int i = 0; i < ships.Count; i++) a.Add(Describe(ships[i]));
            o["ships"] = a;

            // A spawn at a location already holding a fleet of the same faction joins that
            // fleet rather than standing up a new one, so the returned fleet can hold ships
            // this call never created.
            int total = -1;
            try
            {
                List<TISpaceShipState> aboard = fleet.ships;
                if (aboard != null) total = aboard.Count;
            }
            catch (Exception) { }
            o["shipsTotal"] = total >= 0 ? (JToken)new JValue(total) : JValue.CreateNull();
            if (total >= 0 && total != ships.Count) o["merged"] = true;
            return o;
        }

        // Archived without the event: the ship was never announced to anything, so there is
        // nothing that wants to hear it go.
        static void Discard(TISpaceShipState ship)
        {
            try { ship.ArchiveState(false); }
            catch (Exception) { }
            try { GameStateManager.RemoveGameState<TISpaceShipState>(ship.ID, false); }
            catch (Exception) { }
        }

        // CreateAtRunTime dispatches the location by asking it for a hab, orbit, hab site
        // or fleet; a state that answers none of those is silently dropped on the floor.
        static TIGameState Location(int id)
        {
            TIGameState state = ById<TIGameState>(id);
            bool placeable =
                Safe<bool>(delegate { return state.ref_orbit != null; }, false)
                || Safe<bool>(delegate { return state.ref_hab != null; }, false)
                || Safe<bool>(delegate { return state.ref_habSite != null; }, false)
                || Safe<bool>(delegate { return state.ref_fleet != null; }, false);
            if (placeable) return state;
            throw new VerbError("state " + id + " is not an orbit, hab, hab site or fleet location");
        }

        // Default ranking: most weapon mounts, then the newest refit, then data name for a
        // stable answer. Both entry lists are plain fields, so ranking runs no game code.
        static TISpaceShipTemplate Design(TIFactionState faction, string wanted)
        {
            List<TISpaceShipTemplate> designs = faction.shipDesigns;
            if (designs == null || designs.Count == 0)
                throw new VerbError("faction has no ship designs");

            if (!string.IsNullOrEmpty(wanted))
            {
                for (int i = 0; i < designs.Count; i++)
                {
                    TISpaceShipTemplate d = designs[i];
                    if (d == null) continue;
                    if (Same(d.dataName, wanted) || Same(DesignName(d), wanted)) return d;
                }
                throw new VerbError("faction has no design named '" + wanted + "'");
            }

            TISpaceShipTemplate best = null;
            int bestWeapons = -1;
            int bestRefit = -1;
            for (int i = 0; i < designs.Count; i++)
            {
                TISpaceShipTemplate d = designs[i];
                if (d == null) continue;
                int weapons = Weapons(d);
                int refit = 0;
                try { refit = d.refitIteration; }
                catch (Exception) { }
                if (best != null)
                {
                    if (weapons < bestWeapons) continue;
                    if (weapons == bestWeapons && refit < bestRefit) continue;
                    if (weapons == bestWeapons && refit == bestRefit
                        && string.CompareOrdinal(d.dataName, best.dataName) >= 0) continue;
                }
                best = d;
                bestWeapons = weapons;
                bestRefit = refit;
            }
            if (best == null) throw new VerbError("faction has no usable ship design");
            return best;
        }

        static int Weapons(TISpaceShipTemplate d)
        {
            int n = 0;
            try { if (d.noseWeaponTemplateEntries != null) n += d.noseWeaponTemplateEntries.Count; }
            catch (Exception) { }
            try { if (d.hullWeaponTemplateEntries != null) n += d.hullWeaponTemplateEntries.Count; }
            catch (Exception) { }
            return n;
        }

        static string DesignName(TISpaceShipTemplate d)
        {
            return Safe<string>(delegate { return d.displayName; }, null);
        }

        static bool Same(string a, string b)
        {
            return a != null && string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
        }

        static JToken DescribeDesign(TISpaceShipTemplate d)
        {
            var o = new JObject();
            o["dataName"] = d.dataName;
            o["name"] = DesignName(d);
            o["weapons"] = Weapons(d);
            return o;
        }

        static JToken CombatStart(JObject args)
        {
            TISpaceFleetState attacker = Arg<TISpaceFleetState>(args, "attacker");
            TISpaceFleetState defender = Arg<TISpaceFleetState>(args, "defender");
            if (ReferenceEquals(attacker, defender)) throw new VerbError("a fleet cannot attack itself");

            // InitiateCombat does not check that the two fleets are hostile, and
            // InitializeCombat then adds one assets entry and one allowedStances entry per
            // faction with no duplicate guard. One faction on both sides throws on the
            // second add, inside SimulationTimeTick and before the combat is archived, so
            // it re-throws every frame and the strategy clock never runs again.
            TIFactionState attackerFaction = FleetFaction(attacker, "attacker");
            TIFactionState defenderFaction = FleetFaction(defender, "defender");
            if (ReferenceEquals(attackerFaction, defenderFaction))
                throw new VerbError("both fleets belong to " + StateName(attackerFaction)
                    + "; a faction cannot fight itself");

            TIHabState hab = null;
            int habId = OptionalInt(args, "hab");
            if (habId >= 0) hab = ById<TIHabState>(habId);

            bool started = attacker.InitiateCombat(defender, hab, false);

            var o = new JObject();
            o["started"] = started;
            // A false return is not a failure: one combat runs at a time globally, and the
            // request is parked on the fleet until the current one clears.
            o["queued"] = !started;
            TISpaceCombatState combat = FleetCombat(attacker);
            if (combat == null) combat = FleetCombat(defender);
            o["combat"] = combat != null ? Describe(combat) : null;
            return o;
        }

        static TIFactionState FleetFaction(TISpaceFleetState fleet, string role)
        {
            TIFactionState faction = null;
            try { faction = fleet.faction; }
            catch (Exception) { }
            if (faction == null) throw new VerbError(role + " fleet has no faction");
            return faction;
        }

        static TISpaceCombatState FleetCombat(TISpaceFleetState fleet)
        {
            try
            {
                TISpaceCombatState combat = fleet.combatState;
                return combat != null && !combat.archived ? combat : null;
            }
            catch (Exception) { return null; }
        }

        static JToken CombatStatus(JObject args)
        {
            var o = new JObject();
            TISpaceCombatState active = ActiveCombat();
            o["active"] = active != null ? DescribeCombat(active) : null;

            var pending = new JArray();
            foreach (TISpaceCombatState combat in GameStateManager.IterateByClass<TISpaceCombatState>(false))
            {
                if (combat == null || combat.archived) continue;
                if (ReferenceEquals(combat, active)) continue;
                pending.Add(DescribeCombat(combat));
            }
            o["pending"] = pending;

            // Same field query.time reports: an un-archived combat freezes the strategy
            // clock whether or not the speed index changed.
            var manager = PavonisInteractive.TerraInvicta.Systems.GameTime.GameTimeManager.Singleton;
            o["blocked"] = manager != null ? Blocked(manager) : JValue.CreateNull();
            o["autoresolve"] = AutoresolveStatus();
            return o;
        }

        static TISpaceCombatState ActiveCombat()
        {
            try
            {
                TISpaceCombatState combat = TISpaceCombatState.CurrentActiveCombat;
                return combat != null && !combat.archived ? combat : null;
            }
            catch (Exception) { return null; }
        }

        static JToken DescribeCombat(TISpaceCombatState combat)
        {
            var o = new JObject();
            o["id"] = (int)combat.ID;
            o["name"] = StateName(combat);
            o["archived"] = combat.archived;
            Put(o, "initialized", delegate { return (JToken)combat.initialized; });
            Put(o, "active", delegate { return (JToken)combat.active; });
            Put(o, "autoresolve", delegate { return (JToken)combat.autoresolve; });
            Put(o, "autoresolving", delegate { return (JToken)combat.autoresolving; });
            Put(o, "mayRejectAutoresolve", delegate { return (JToken)combat.mayRejectAutoresolve; });
            Put(o, "combatOccurs", delegate { return (JToken)combat.combatOccurs; });
            Put(o, "requiresBidding", delegate { return (JToken)combat.requiresBidding; });
            Put(o, "stancesSelected", delegate { return (JToken)combat.HaveStancesBeenSelected; });
            Put(o, "bidsSubmitted", delegate { return (JToken)combat.HaveBidsBeenSubmitted; });
            Put(o, "simulated", delegate { return (JToken)(combat.SimulatedCombat != null); });

            bool playerInvolved = false;
            var participants = new JArray();
            TIFactionState[] factions = CombatFactions(combat);
            for (int i = 0; i < factions.Length; i++)
            {
                TIFactionState f = factions[i];
                if (f == null) continue;
                var p = new JObject();
                p["faction"] = Describe(f);
                bool ai = IsAI(f);
                p["ai"] = ai;
                bool activePlayer = IsActivePlayer(f);
                p["activePlayer"] = activePlayer;
                if (activePlayer) playerInvolved = true;
                CombatStance? stance = StanceOf(combat, f);
                p["stance"] = stance != null
                    ? (JToken)new JValue(stance.Value.ToString()) : JValue.CreateNull();
                p["bid"] = BidOf(combat, f);
                participants.Add(p);
            }
            o["participants"] = participants;
            o["playerInvolved"] = playerInvolved;

            var fleets = new JArray();
            try
            {
                TISpaceFleetState[] list = combat.fleets;
                if (list != null)
                {
                    for (int i = 0; i < list.Length; i++)
                        fleets.Add(list[i] != null ? Describe(list[i]) : (JToken)JValue.CreateNull());
                }
            }
            catch (Exception) { }
            o["fleets"] = fleets;
            Put(o, "hab", delegate { return combat.hab != null ? Describe(combat.hab) : (JToken)null; });
            return o;
        }

        static TIFactionState[] CombatFactions(TISpaceCombatState combat)
        {
            try
            {
                TIFactionState[] factions = combat.factions;
                return factions != null ? factions : new TIFactionState[0];
            }
            catch (Exception) { return new TIFactionState[0]; }
        }

        // Null when the faction has no entry in the stance dictionary at all.
        static CombatStance? StanceOf(TISpaceCombatState combat, TIFactionState faction)
        {
            try
            {
                Dictionary<TIFactionState, CombatStance> stances = combat.stances;
                CombatStance stance;
                if (stances != null && stances.TryGetValue(faction, out stance)) return stance;
            }
            catch (Exception) { }
            return null;
        }

        // NotYetSet is a placeholder the game writes into the dictionary, and its own
        // HaveStancesBeenSelected rejects it, so presence alone does not mean submitted.
        static bool StanceSet(TISpaceCombatState combat, TIFactionState faction)
        {
            CombatStance? stance = StanceOf(combat, faction);
            return stance != null && stance.Value != CombatStance.NotYetSet;
        }

        // A bid of zero is a real bid, so bids go by presence.
        static bool HasBid(TISpaceCombatState combat, TIFactionState faction)
        {
            try
            {
                Dictionary<TIFactionState, float> bids = combat.bids_kps;
                float bid;
                return bids != null && bids.TryGetValue(faction, out bid);
            }
            catch (Exception) { return false; }
        }

        static JToken BidOf(TISpaceCombatState combat, TIFactionState faction)
        {
            try
            {
                Dictionary<TIFactionState, float> bids = combat.bids_kps;
                float bid;
                if (bids != null && bids.TryGetValue(faction, out bid)) return Num(bid);
            }
            catch (Exception) { }
            return JValue.CreateNull();
        }

        static bool IsAI(TIFactionState faction)
        {
            try
            {
                TIPlayerState player = faction.player;
                return player == null || player.isAI;
            }
            catch (Exception) { return true; }
        }

        static bool IsActivePlayer(TIFactionState faction)
        {
            return Safe<bool>(delegate { return faction.isActivePlayer; }, false);
        }

        static TIFactionState ActivePlayer()
        {
            try
            {
                GameControl control = GameControl.control;
                return control != null ? control.activePlayer : null;
            }
            catch (Exception) { return null; }
        }

        // Armed autoresolve. Held as an int id, never a game object, so a scene reload
        // between frames leaves nothing stale behind.
        enum AutoPhase { Idle, Settling, Firing, Simulating, Closing }

        static int autoCombatId;
        static AutoPhase autoPhase;
        static int autoFrames;
        static string autoError;
        static string autoNote;
        // -1 = no override; otherwise a CombatStance value requested by the client.
        static int autoStance = -1;
        // Cached across the frames of one armed autoresolve: FindObjectOfType walks
        // the scene, and the tick would otherwise pay that walk every frame.
        static PrecombatController autoPrecombat;

        static JToken CombatAutoresolve(JObject args)
        {
            TISpaceCombatState combat = TargetCombat(args);
            if (combat == null) throw new VerbError("no combat");

            if (autoPhase != AutoPhase.Idle && autoCombatId != (int)combat.ID)
                throw new VerbError("already resolving combat " + autoCombatId);

            // factions is only filled by InitializeCombat, so before promotion every
            // combat looks AI-only. Arm regardless and settle the question in SettleStep,
            // which already waits for initialized.
            if (Initialized(combat) && !PlayerInvolved(combat))
            {
                // Both sides are AI: the precombat listeners answer their own stance
                // prompts, OnPrecombatComplete runs Autoresolve, and the simulation
                // callback applies the result. Nothing for the bridge to do.
                var auto = new JObject();
                auto["armed"] = false;
                auto["resolving"] = "automatic";
                auto["combat"] = Describe(combat);
                return auto;
            }

            if (autoPhase == AutoPhase.Idle)
            {
                autoCombatId = (int)combat.ID;
                autoFrames = 0;
                autoError = null;
                autoNote = null;
                autoStance = ParseStance(args);
                // A combat already carrying a simulation only needs the apply step.
                autoPhase = Simulated(combat) ? AutoPhase.Simulating : AutoPhase.Settling;
            }
            return AutoresolveStatus();
        }

        static bool Initialized(TISpaceCombatState combat)
        {
            try { return combat.initialized; }
            catch (Exception) { return false; }
        }

        static bool Simulated(TISpaceCombatState combat)
        {
            try { return combat.autoresolving || combat.SimulatedCombat != null; }
            catch (Exception) { return false; }
        }

        static bool PlayerInvolved(TISpaceCombatState combat)
        {
            TIFactionState[] factions = CombatFactions(combat);
            for (int i = 0; i < factions.Length; i++)
            {
                if (factions[i] != null && IsActivePlayer(factions[i])) return true;
            }
            return false;
        }

        static TISpaceCombatState TargetCombat(JObject args)
        {
            int id = OptionalInt(args, "combat");
            if (id >= 0)
            {
                TISpaceCombatState combat = ById<TISpaceCombatState>(id);
                if (combat.archived) throw new VerbError("combat " + id + " is archived");
                return combat;
            }
            TISpaceCombatState active = ActiveCombat();
            if (active != null) return active;
            foreach (TISpaceCombatState combat in GameStateManager.IterateByClass<TISpaceCombatState>(false))
            {
                if (combat != null && !combat.archived) return combat;
            }
            return null;
        }

        static JToken AutoresolveStatus()
        {
            var o = new JObject();
            o["armed"] = autoPhase != AutoPhase.Idle;
            o["phase"] = autoPhase.ToString().ToLowerInvariant();
            o["combat"] = autoPhase != AutoPhase.Idle
                ? (JToken)new JValue(autoCombatId) : JValue.CreateNull();
            o["frames"] = autoFrames;
            o["error"] = autoError;
            o["note"] = autoNote;
            return o;
        }

        static void Disarm()
        {
            autoCombatId = 0;
            autoPhase = AutoPhase.Idle;
            autoFrames = 0;
            autoStance = -1;
            autoPrecombat = null;
        }

        // Optional client stance override: {"stance": "Pursue"}. Validated against the
        // enum here and against allowedStances at submit time.
        static int ParseStance(JObject args)
        {
            JToken t = args["stance"];
            if (t == null || t.Type == JTokenType.Null) return -1;
            string s = t.ToString();
            if (string.Equals(s, "Pursue", StringComparison.OrdinalIgnoreCase)) return (int)CombatStance.Pursue;
            if (string.Equals(s, "Defend", StringComparison.OrdinalIgnoreCase)) return (int)CombatStance.Defend;
            if (string.Equals(s, "Evade", StringComparison.OrdinalIgnoreCase)) return (int)CombatStance.Evade;
            throw new VerbError("unknown stance '" + s + "' (Pursue, Defend, Evade)");
        }

        // Main thread, once a frame. Every step is guarded so a throw disarms rather than
        // leaving half-armed state behind, and the phase advances before any call that
        // must not run twice.
        static void TickAutoresolve()
        {
            if (autoPhase == AutoPhase.Idle) return;
            try
            {
                if (!HasCampaign) { Disarm(); return; }

                if (++autoFrames > AutoresolveFrameBudget)
                    throw new VerbError("autoresolve stalled in phase "
                        + autoPhase.ToString().ToLowerInvariant());

                // The closing phase outlives the combat: applying the result archives it
                // and drops it from the manager, so this runs before the lookup.
                if (autoPhase == AutoPhase.Closing) { CloseStep(); return; }

                // Null is a live outcome here (a resolved combat leaves the manager),
                // so this lookup does not go through ById.
                var combat = GameStateManager.FindGameState<TISpaceCombatState>(
                    new GameStateID(autoCombatId), true);
                if (combat == null || combat.archived)
                {
                    // A successful evade ends the combat with no simulation: the game
                    // archives it and leaves the escape report on the precombat canvas,
                    // which freezes the clock until closed.
                    if (CanvasUp(FindPrecombat()))
                    {
                        autoPhase = AutoPhase.Closing;
                        autoNote = "combat ended before simulation";
                        return;
                    }
                    Disarm();
                    return;
                }

                switch (autoPhase)
                {
                    case AutoPhase.Settling: SettleStep(combat); break;
                    case AutoPhase.Firing: FireStep(combat); break;
                    case AutoPhase.Simulating: WaitStep(combat); break;
                }
            }
            catch (Exception e)
            {
                autoError = e is VerbError ? e.Message : Note(e);
                autoNote = null;
                Disarm();
            }
        }

        // Everything the precombat screen would have collected from the player, collected
        // through the same buttons. HandlePrompts only answers prompts for AI factions, so
        // the wait loop inside OnPrecombatComplete may only ever be left waiting on AI.
        static void SettleStep(TISpaceCombatState combat)
        {
            if (Simulated(combat)) { autoPhase = AutoPhase.Simulating; return; }
            if (!combat.initialized) return;
            // Now that factions is populated the question can finally be answered.
            if (!PlayerInvolved(combat))
            {
                Disarm();
                autoNote = "resolving automatically";
                return;
            }

            PrecombatController precombat = Precombat();
            if (!ReferenceEquals(precombat.combat, combat)) return;

            combat.autoresolve = true;

            TIFactionState player = ControllerPlayer(precombat);
            if (combat.IncludesFaction(player) && !StanceSet(combat, player))
                precombat.StanceSubmit((int)Stance(combat, player));

            if (!combat.HaveStancesBeenSelected) { RequireAIOnly(combat, false); return; }

            if (combat.requiresBidding && !combat.HaveBidsBeenSubmitted)
            {
                if (combat.IncludesFaction(player) && !HasBid(combat, player))
                    precombat.BidSubmit();
                if (!combat.HaveBidsBeenSubmitted) { RequireAIOnly(combat, true); return; }
            }

            autoPhase = AutoPhase.Firing;
        }

        static void FireStep(TISpaceCombatState combat)
        {
            PrecombatController precombat = Precombat();
            if (!ReferenceEquals(precombat.combat, combat))
                throw new VerbError("precombat controller lost the combat");
            // Advance first: EndPrecombatInteraction is a one-shot, and a throw out of the
            // event chain must not leave the phase armed to fire it again.
            autoPhase = AutoPhase.Simulating;
            precombat.AutoresolveSelected();
        }

        static void WaitStep(TISpaceCombatState combat)
        {
            if (combat.autoresolving) return;
            if (combat.SimulatedCombat == null) return;
            PrecombatController precombat = Precombat();
            if (!ReferenceEquals(precombat.combat, combat))
                throw new VerbError("precombat controller lost the combat");
            ControllerPlayer(precombat);
            // Advance first: ApplySimulatedCombat writes damage into the real states and
            // must never run twice.
            autoPhase = AutoPhase.Closing;
            // The button's own chain: clears PromptBeginCombat, hides the accept panel,
            // and applies the simulation.
            precombat.OnAcceptAutoresolveSelected();
        }

        // Applying the result raises CombatEnds, which puts the post-combat report on the
        // precombat canvas and leaves Canvas.enabled true. SimulationTimeTick returns early
        // while that canvas is enabled, so until it is closed no further combat is promoted
        // and the clock never advances. Which close button is live depends on the outcome:
        // a fought combat shows the post-combat report, an escape shows the main panel in
        // report mode. The Autopilot dispatches on whichever is clickable; so does this.
        // Both closes also replay a combat that was initiated while the report was up.
        static void CloseStep()
        {
            PrecombatController precombat = Precombat();
            if (!CanvasUp(precombat)) { Disarm(); return; }
            if (Clickable(precombat.postCombatCloseButton))
            {
                Disarm();
                precombat.OnClosePostCombatButtonSelected();
                return;
            }
            if (Clickable(precombat.closeButton))
            {
                Disarm();
                precombat.CloseResolveSelected();
                return;
            }
            // Neither button is up yet; the frame budget bounds this wait.
        }

        static bool CanvasUp(PrecombatController precombat)
        {
            try
            {
                if (precombat == null) return false;
                UnityEngine.Canvas canvas = precombat.Canvas;
                return canvas != null && canvas.enabled;
            }
            catch (Exception) { return false; }
        }

        // Waiting is only safe while every outstanding stance or bid belongs to an AI
        // faction; a human one would never be answered and the wait loop would hang.
        static void RequireAIOnly(TISpaceCombatState combat, bool bids)
        {
            TIFactionState[] factions = CombatFactions(combat);
            for (int i = 0; i < factions.Length; i++)
            {
                TIFactionState f = factions[i];
                if (f == null) continue;
                bool have = bids ? HasBid(combat, f) : StanceSet(combat, f);
                if (have) continue;
                if (!IsAI(f))
                    throw new VerbError("combat needs a " + (bids ? "bid" : "stance")
                        + " from non-AI faction " + StateName(f));
            }
        }

        static CombatStance Stance(TISpaceCombatState combat, TIFactionState faction)
        {
            try
            {
                Dictionary<TIFactionState, List<CombatStance>> allowed = combat.allowedStances;
                List<CombatStance> list;
                if (allowed != null && allowed.TryGetValue(faction, out list) && list != null)
                {
                    if (autoStance >= 0)
                    {
                        for (int i = 0; i < list.Count; i++)
                        {
                            if ((int)list[i] == autoStance) return (CombatStance)autoStance;
                        }
                    }
                    for (int i = 0; i < list.Count; i++)
                    {
                        if (list[i] == CombatStance.Defend) return CombatStance.Defend;
                    }
                    for (int i = 0; i < list.Count; i++)
                    {
                        if (list[i] != CombatStance.NotYetSet) return list[i];
                    }
                }
            }
            catch (Exception) { }
            // What the vanilla Autopilot submits when it drives the precombat screen.
            return CombatStance.Defend;
        }

        // No Singleton on this controller, and its canvas is disabled for AI-only
        // combats, so inactive objects have to be searched too. Unity fakes null on a
        // destroyed component, so the cache liveness check is the bool operator.
        static PrecombatController FindPrecombat()
        {
            if (autoPrecombat) return autoPrecombat;
            autoPrecombat = Find<PrecombatController>();
            return autoPrecombat;
        }

        static PrecombatController Precombat()
        {
            PrecombatController precombat = FindPrecombat();
            if (precombat == null) throw new VerbError("no precombat controller");
            return precombat;
        }

        // The precombat buttons submit on behalf of the controller's own activePlayer,
        // which is copied from GameControl by SetActivePlayer. A controller that has not
        // caught up would answer for the wrong faction.
        static TIFactionState ControllerPlayer(PrecombatController precombat)
        {
            TIFactionState player = precombat.activePlayer;
            if (player == null) throw new VerbError("precombat controller has no active player");
            if (!ReferenceEquals(player, ActivePlayer()))
                throw new VerbError("precombat controller has a stale active player");
            return player;
        }

        #region fleet.bombard

        // The three concrete BombardOperation subclasses differ only in the altitude
        // they hand InitiateBombardment: TIGlobalConfig low/med/highBombardmentAltitude_km,
        // 200/400/600 km on the shipped values. Closer is more damage per hit and more
        // exposure to the target's own defenses.
        static readonly Dictionary<string, Type> BombardTiers = BuildBombardTiers();

        static Dictionary<string, Type> BuildBombardTiers()
        {
            var t = new Dictionary<string, Type>(StringComparer.OrdinalIgnoreCase);
            t["low"] = typeof(BombardOperation_Low);
            t["med"] = typeof(BombardOperation_Med);
            t["high"] = typeof(BombardOperation_High);
            return t;
        }

        // Possible targets are listed in the refusal message; on Earth the list is every
        // region the fleet may hit, which is most of the map.
        const int MaxBombardTargetsNamed = 10;

        static JToken FleetBombard(JObject args)
        {
            TISpaceFleetState fleet = Arg<TISpaceFleetState>(args, "fleet");
            TIGameState target = Arg<TIGameState>(args, "target");
            string altitude = Str(args, "altitude");
            if (string.IsNullOrEmpty(altitude)) altitude = "low";
            BombardOperation op = BombardOp(altitude);

            // The engine's own non-UI order path is
            // AIDailyFactionPlanner.SingleFleetOperation: test the target against
            // GetPossibleTargets, then OnOperationConfirm(actor, target, null, null).
            // ActorCanPerformOperation is the gate the UI applies before it offers the
            // order at all, and it subsumes every fleet-side precondition, so it runs
            // first here to turn one refusing bool into a message naming the state.
            if (!op.ActorCanPerformOperation(fleet, target))
                throw new VerbError("fleet " + (int)fleet.ID + " cannot bombard: "
                    + BombardRefusal(fleet, op, target));

            List<TIGameState> possible = null;
            try { possible = op.GetPossibleTargets(fleet, null); }
            catch (Exception e) { throw new VerbError("GetPossibleTargets threw: " + Note(e)); }
            if (possible == null || !possible.Contains(target))
                throw new VerbError(StateName(target) + " (" + (int)target.ID
                    + ") is not a possible bombardment target for fleet " + (int)fleet.ID
                    + "; " + BombardTargetList(possible));

            // OnOperationConfirm's first act is ValidOperation, which runs before
            // anything is written, so a false return leaves no half-applied order.
            // Confirming against a human nation's region also commits an atrocity
            // (AtrocityCause.SpaceBombardHumanNationRegions) before the bombardment
            // starts -- permanent campaign state, reported below.
            bool atrocity = target.isRegionState && !target.ref_nation.alienNation;
            if (!op.OnOperationConfirm(fleet, target, null, null))
                throw new VerbError("the engine refused the bombardment order: "
                    + BombardRefusal(fleet, op, target));
            if (!fleet.bombarding)
                throw new VerbError("OnOperationConfirm returned true but the fleet is "
                    + "not bombarding; the engine's write order has changed -- treat the "
                    + "order as suspect and inspect the fleet's operations");

            var o = new JObject();
            o["atrocityCommitted"] = atrocity;
            o["fleet"] = Describe(fleet);
            o["target"] = Describe(target);
            o["operation"] = op.GetType().Name;
            // Read back from the fleet rather than recomputed: this is the state the
            // caller will be polling.
            Put(o, "bombarding", delegate { return (JToken)fleet.bombarding; });
            Put(o, "bombardmentTarget", delegate
            {
                TIGameState t = fleet.bombardmentTarget;
                return t != null ? Describe(t) : JValue.CreateNull();
            });
            Put(o, "altitudeKm", delegate { return (JToken)fleet.bombardmentAltitude_km; });
            Put(o, "endBombardmentReason",
                delegate { return (JToken)fleet.endBombardmentReason.ToString(); });

            // Bombardment runs for BOMBARDMENT_DURATION_DAYS and is then ended by the
            // operation's own ExecuteOperation, so the caller advances the clock and
            // re-reads the fleet.
            OperationData data = BombardOperationData(fleet, op, target);
            o["durationDays"] = BombardOperation.BOMBARDMENT_DURATION_DAYS;
            Put(o, "completes", delegate
            {
                return data != null && data.completionDate != null
                    ? (JToken)InvariantDate(data.completionDate) : JValue.CreateNull();
            });

            // Only when the TARGET is the hab. A landed-fleet target also resolves a
            // ref_hab (the site's hab hosting it), but that hab is not what is being
            // bombarded and polling it would watch the wrong object.
            TIHabState hab = target as TIHabState;
            if (hab != null)
            {
                var h = new JObject();
                Put(h, "underBombardment", delegate { return (JToken)hab.underBombardment; });
                // Each hit picks one weighted-random module out of OkayModules() less
                // the core, so a named module is destroyed by polling this count and
                // repeating, never by asking for it. When okayModules reaches zero the
                // next hit destroys the hab outright.
                Put(h, "okayModules", delegate { return (JToken)hab.OkayModules().Count; });
                Put(h, "presentModules", delegate { return (JToken)hab.PresentModules().Count; });
                o["hab"] = h;
            }
            return o;
        }

        // The registry instance, never a fresh one: the interrupt check resolves a
        // blocking operation's BreakthroughOps through this same dictionary and compares
        // instances, and OperationData stores the instance for the cancel path, so a
        // private copy would compare unequal to itself everywhere the engine looks.
        static BombardOperation BombardOp(string altitude)
        {
            Type type;
            if (!BombardTiers.TryGetValue(altitude, out type))
                throw new VerbError("arg 'altitude' must be low, med or high");
            var lookup = OperationsManager.operationsLookup;
            IOperation op = null;
            if (lookup == null || !lookup.TryGetValue(type, out op) || op == null)
                throw new VerbError("OperationsManager has no " + type.Name + " registered");
            var bombard = op as BombardOperation;
            if (bombard == null)
                throw new VerbError("OperationsManager holds a " + op.GetType().Name
                    + " under " + type.Name);
            return bombard;
        }

        // Every clause ActorCanPerformOperation reads, reported as observed rather than
        // guessed at: the method answers one bool, and these values are what a caller
        // needs to see to fix the setup.
        static string BombardRefusal(TISpaceFleetState fleet, BombardOperation op,
                                     TIGameState target)
        {
            var parts = new List<string>();
            parts.Add("transferAssigned="
                + Safe<bool>(delegate { return fleet.transferAssigned; }, false));
            parts.Add("dockedOrLanded="
                + Safe<bool>(delegate { return fleet.dockedOrLanded; }, false));
            parts.Add("orbitBody=" + Safe<string>(
                delegate { return StateName(fleet.orbitState.barycenter); }, "?"));
            parts.Add("interfaceOrbit="
                + Safe<bool>(delegate { return fleet.orbitState.interfaceOrbit; }, false));
            // A synchronous orbit (GEO, ASO) is refused outright.
            parts.Add("synchOrbit="
                + Safe<bool>(delegate { return fleet.orbitState.template.synch; }, false));
            parts.Add("inCombat="
                + Safe<bool>(delegate { return fleet.inCombatOrWaitingForCombat; }, false));
            // Zero means no ship in the fleet carries a weapon that reaches the surface.
            parts.Add("bombardmentValue=" + Safe<float>(
                delegate { return fleet.BombardmentValue(fleet.ref_spaceBody); }, 0f)
                .ToString(CultureInfo.InvariantCulture));
            parts.Add("blockedBy=" + (BombardBlocker(fleet, op) ?? "none"));
            parts.Add("possibleTargets=" + Safe<int>(
                delegate { return op.GetPossibleTargets(fleet, null).Count; }, -1));
            if (target != null)
                parts.Add("targetArchived="
                    + Safe<bool>(delegate { return target.archived; }, false));
            return string.Join(" ", parts.ToArray());
        }

        // The operation already on the fleet that would refuse the interrupt: blocking,
        // and not one this operation is allowed to break through.
        static string BombardBlocker(TISpaceFleetState fleet, BombardOperation op)
        {
            try
            {
                List<OperationData> current = fleet.CurrentOperations();
                if (current == null) return null;
                for (int i = 0; i < current.Count; i++)
                {
                    OperationData d = current[i];
                    if (d == null || d.operation == null) continue;
                    if (!d.operation.IsBlockingOperation()) continue;
                    var fleetOp = d.operation as TISpaceFleetOperationTemplate;
                    if (fleetOp != null && BreaksThrough(fleetOp, op)) continue;
                    return d.operation.GetType().Name;
                }
            }
            catch (Exception) { }
            return null;
        }

        static bool BreaksThrough(TISpaceFleetOperationTemplate blocking, BombardOperation op)
        {
            List<Type> allowed = blocking.BreakthroughOps();
            if (allowed == null) return false;
            var lookup = OperationsManager.operationsLookup;
            for (int i = 0; i < allowed.Count; i++)
            {
                IOperation instance = null;
                if (lookup != null && lookup.TryGetValue(allowed[i], out instance)
                    && ReferenceEquals(instance, op)) return true;
            }
            return false;
        }

        static string BombardTargetList(List<TIGameState> possible)
        {
            if (possible == null || possible.Count == 0)
                return "the fleet has no possible bombardment targets here "
                    + "(habs are only targets at bodies other than Earth)";
            var names = new List<string>();
            int n = possible.Count < MaxBombardTargetsNamed
                ? possible.Count : MaxBombardTargetsNamed;
            for (int i = 0; i < n; i++)
            {
                TIGameState t = possible[i];
                if (t == null) continue;
                names.Add(StateName(t) + " (" + (int)t.ID + ")");
            }
            return "possible (" + possible.Count + "): " + string.Join(", ", names.ToArray())
                + (possible.Count > n ? ", ..." : "");
        }

        // OperationConfirmed appends, and BombardOperation is Repeatable, so the last
        // match is the order this call placed.
        static OperationData BombardOperationData(TISpaceFleetState fleet, IOperation op,
                                                  TIGameState target)
        {
            try
            {
                List<OperationData> current = fleet.CurrentOperations();
                if (current == null) return null;
                for (int i = current.Count - 1; i >= 0; i--)
                {
                    OperationData d = current[i];
                    if (d == null) continue;
                    if (!ReferenceEquals(d.operation, op)) continue;
                    if (!ReferenceEquals(d.target, target)) continue;
                    return d;
                }
            }
            catch (Exception) { }
            return null;
        }

        #endregion
    }
}

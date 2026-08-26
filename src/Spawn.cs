using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using PavonisInteractive.TerraInvicta;

namespace TerraInvictaMCP
{
    // spawn.hab, spawn.module, spawn.army, spawn.councilor, spawn.alien_site.
    //
    // Every one of these takes the game's own construction path with the cost,
    // prerequisite and build-time gates left out. That makes them fixtures: a spawn
    // proves a mechanism fires, never that the content is reachable through play.
    public static partial class Verbs
    {
        // Bounds the module list before any of it is resolved. The hab path refuses
        // what does not fit rather than overflowing, so this only keeps an absurd
        // request from turning into an absurd number of template lookups.
        const int MaxSpawnModules = 32;

        // Construction time handed to InitiateConstructModule. Zero dates the module
        // complete now; the method reads any negative value as "use the template's own
        // build time", which is what an uncompleted spawn should leave behind.
        const double BuildNow = 0.0;
        const double BuildNormally = -1.0;

        #region spawn.hab

        static JToken SpawnHab(JObject args)
        {
            TIFactionState faction = Arg<TIFactionState>(args, "faction");
            TIGameState selected = Arg<TIGameState>(args, "location");
            int tier = Int(args, "tier");
            if (tier < 1 || tier > 3) throw new VerbError("arg 'tier' must be 1..3");
            List<string> modules = ModuleNames(args, "modules");
            bool complete = Bool(args, "complete", true);

            // Founding the site and registering the hab happen in one breath, so every
            // name is resolved first: a template miss part way through would leave a
            // hab behind that the call then has to refuse.
            for (int i = 0; i < modules.Count; i++) ModuleTemplate(modules[i]);

            TIGameState location = FoundSite(selected);

            // The module list is deliberately NOT handed to InitializeNewHab. Its
            // founding loop walks sectors as it fills slots, and when it runs out of
            // empty active sectors it resets the slot cursor to zero in the sector it
            // is already in, overwriting what is there. At tier 1 the first thing it
            // overwrites is the core module, and it reports nothing. Modules go in
            // afterwards through AvailableSlots, which can only ever return an empty
            // or destroyed slot.
            TIHabState hab = null;
            try
            {
                hab = GameStateManager.CreateNewGameState<TIHabState>();
                hab.InitializeNewHab(faction, location, faction, tier, 0f, null);
            }
            catch (Exception)
            {
                // Best effort: the sector and module states InitializeNewHab creates
                // before a throw are registered separately and are not reachable from
                // here, so this drops the hab and leaves those orphaned rather than
                // leaving a half-built hab in play.
                if (hab != null) DiscardState<TIHabState>(hab);
                throw;
            }

            // The hab is registered from here on, so nothing below may leave through an
            // error frame: a caller told only "failed" would never learn a real hab is
            // standing. A module that throws joins the ones that did not fit.
            var missed = new JArray();
            var failures = new List<string>();
            int installed = 0;
            for (int i = 0; i < modules.Count; i++)
            {
                try
                {
                    if (Install(hab, modules[i], complete) != null) installed++;
                    else missed.Add(new JValue(modules[i]));
                }
                catch (Exception e)
                {
                    missed.Add(new JValue(modules[i]));
                    failures.Add(modules[i] + ": " + (e is VerbError ? e.Message : Note(e)));
                }
            }
            if (complete)
            {
                try { CompleteHab(hab); }
                catch (Exception e)
                {
                    failures.Add("completion: " + (e is VerbError ? e.Message : Note(e)));
                }
            }

            var o = new JObject();
            o["id"] = (int)hab.ID;
            o["name"] = StateName(hab);
            Put(o, "tier", delegate { return (JToken)hab.tier; });
            Put(o, "habType", delegate { return (JToken)hab.habType.ToString(); });
            o["faction"] = Describe(faction);
            o["location"] = Describe(location);
            o["completed"] = complete;
            o["modules"] = HabModules(hab);
            o["modulesRequested"] = modules.Count;
            o["modulesInstalled"] = installed;
            o["modulesNotInstalled"] = missed;
            // The hab is real and usable either way, so a shortfall is reported rather
            // than thrown; a caller that only checks ok would otherwise never learn the
            // fixture came out smaller than it asked for.
            if (missed.Count > 0 || failures.Count > 0)
            {
                string warning = missed.Count + " of " + modules.Count
                    + " modules were not installed (the hab holds what its tier's"
                    + " active sectors hold)";
                for (int i = 0; i < failures.Count; i++) warning += "; " + failures[i];
                o["warning"] = warning;
            }
            return o;
        }

        // Resolves the requested location to somewhere InitializeNewHab accepts. A hab
        // site or an orbit is used directly; a body or Lagrange point falls back to its
        // first free ground site, else its first orbit. Bodies and Lagrange points both
        // carry an orbits list.
        static TIGameState FoundSite(TIGameState selected)
        {
            if (Safe<bool>(delegate { return selected.isHabSiteState; }, false))
            {
                TIHabSiteState site = selected.ref_habSite;
                if (site == null) throw new VerbError("hab site state has no site");
                if (site.hab != null) throw new VerbError("site is occupied");
                // pendingHab marks a site an AI has committed a founding fleet to.
                // Founding here clears the marker, and the fleet that arrives later
                // overwrites the site's hab reference, orphaning whatever this spawned.
                if (Safe<bool>(delegate { return site.pendingHab; }, false))
                    throw new VerbError("site is reserved for a hab already on its way");
                site.FoundHab();
                return site;
            }
            if (Safe<bool>(delegate { return selected.isOrbitState; }, false))
            {
                TIOrbitState orbit = selected.ref_orbit;
                if (orbit == null) throw new VerbError("orbit state has no orbit");
                ClaimOrbit(orbit);
                return orbit;
            }

            TINaturalSpaceObjectState body = Safe<TINaturalSpaceObjectState>(delegate
            {
                return selected.isNaturalSpaceObjectState
                    ? selected.ref_naturalSpaceObject : null;
            }, null);
            if (body == null)
                throw new VerbError("state " + (int)selected.ID + " is not a hab site, "
                    + "orbit, Lagrange point, or planet/moon");

            TIHabSiteState ground = FreeSite(body);
            if (ground != null)
            {
                ground.FoundHab();
                return ground;
            }
            TIOrbitState first = FirstOrbit(body);
            if (first == null)
                throw new VerbError("no free site or orbit at " + StateName(body));
            ClaimOrbit(first);
            return first;
        }

        // TIOrbitState.FoundHab consumes a pending-station marker, the one the AI sets
        // when it commits to building there. A spawn never sets one, and the counter is
        // added to the orbit's station count in its capacity check, so decrementing from
        // zero would quietly buy that orbit an extra station slot for the rest of the
        // campaign. TIHabSiteState.FoundHab is a plain bool clear and needs no guard.
        static void ClaimOrbit(TIOrbitState orbit)
        {
            if (Safe<int>(delegate { return orbit.pendingHabs; }, 0) > 0) orbit.FoundHab();
        }

        // Stricter than the engine's own vacantHabSites, which only rejects a site whose
        // hab has present modules: a site holding a module-less hab reads as vacant
        // there, and a site an AI has reserved reads as vacant until its fleet arrives.
        // Founding on either overwrites the site's hab reference and orphans a hab. The
        // direct hab-site path refuses both cases too.
        static TIHabSiteState FreeSite(TINaturalSpaceObjectState body)
        {
            try
            {
                if (!body.isSpaceBodyState) return null;
                List<TIHabSiteState> sites = body.ref_spaceBody.vacantHabSites;
                if (sites == null) return null;
                for (int i = 0; i < sites.Count; i++)
                {
                    TIHabSiteState site = sites[i];
                    if (site != null && site.hab == null && !site.pendingHab) return site;
                }
            }
            catch (Exception) { }
            return null;
        }

        static TIOrbitState FirstOrbit(TINaturalSpaceObjectState body)
        {
            try
            {
                List<TIOrbitState> orbits = body.orbits;
                if (orbits == null) return null;
                for (int i = 0; i < orbits.Count; i++)
                {
                    if (orbits[i] != null) return orbits[i];
                }
            }
            catch (Exception) { }
            return null;
        }

        // Runs after the requested modules are in, so unlike the game's founding path
        // the core is completed last of the two groups. Harmless: completing the core
        // recomputes power management across the whole hab, so modules that went in
        // while it was still under construction are picked up. Within this method the
        // core still goes first, because completing it writes the hab's tier from the
        // core template; taking the pending list afterwards means it is already out.
        static void CompleteHab(TIHabState hab)
        {
            TIHabModuleState core = Safe<TIHabModuleState>(
                delegate { return hab.coreModule; }, null);
            if (core != null) hab.CompleteModuleConstruction(core);

            List<TIHabModuleState> pending = hab.UnderConstructionModules();
            if (pending == null) return;
            for (int i = 0; i < pending.Count; i++)
            {
                TIHabModuleState module = pending[i];
                if (module == null || ReferenceEquals(module, core)) continue;
                hab.CompleteModuleConstruction(module);
            }
        }

        // Installed modules only: the empty slots are the difference between this and
        // the hab's slot count, and listing them would bury the answer.
        static JArray HabModules(TIHabState hab)
        {
            var a = new JArray();
            AddModules(a, Safe<List<TIHabModuleState>>(
                delegate { return hab.CompletedModules(); }, null));
            AddModules(a, Safe<List<TIHabModuleState>>(
                delegate { return hab.UnderConstructionModules(); }, null));
            return a;
        }

        static void AddModules(JArray a, List<TIHabModuleState> modules)
        {
            if (modules == null) return;
            for (int i = 0; i < modules.Count; i++)
            {
                if (modules[i] != null) a.Add(DescribeModule(modules[i]));
            }
        }

        // One shape for every verb that hands a module back, so spawn.module,
        // kill.module, module.power and spawn.hab's module list all agree.
        //
        // The power block is what `module.power` reads its gates from, reported
        // here so a caller sees a refusal coming instead of discovering it. Each
        // predicate is the engine's own, called rather than reimplemented; each
        // read is guarded, so a module in a state one of them dislikes nulls that
        // field and keeps the rest.
        static JToken DescribeModule(TIHabModuleState module)
        {
            var o = new JObject();
            o["id"] = (int)module.ID;
            o["name"] = StateName(module);
            Put(o, "slot", delegate { return (JToken)module.slot; });
            // slot is an index within a sector and repeats across a hab, so the
            // pair is what locates a module the way the listings print it.
            Put(o, "sectorNum", delegate { return (JToken)module.sectorNum; });
            Put(o, "template", delegate
            {
                TIHabModuleTemplate t = module.moduleTemplate;
                return t != null ? (JToken)new JValue(t.dataName) : JValue.CreateNull();
            });
            Put(o, "completed", delegate { return (JToken)module.completed; });
            Put(o, "underConstruction", delegate { return (JToken)module.underConstruction; });
            Put(o, "destroyed", delegate { return (JToken)module.destroyed; });
            Put(o, "decommissioning", delegate { return (JToken)module.decommissioning; });
            Put(o, "powered", delegate { return (JToken)module.powered; });
            // The engine's own `active`, which is exactly functional && powered.
            Put(o, "active", delegate { return (JToken)module.active; });
            // ModulePower(), not template.power: solar output and the escape-
            // velocity power requirement are special rules routed through
            // SolarPowerOutput and EscapeVelocityBasedPowerRequirement, and the
            // template number misses both. Positive produces, negative consumes.
            Put(o, "power", delegate { return (JToken)module.ModulePower(); });
            Put(o, "canPower", delegate { return (JToken)module.CanPower(); });
            Put(o, "canDepower", delegate { return (JToken)module.CanDepower(); });
            Put(o, "canTurnOff", delegate
            {
                TIHabModuleTemplate t = module.moduleTemplate;
                return t != null ? (JToken)new JValue(t.CanTurnOff) : JValue.CreateNull();
            });
            Put(o, "buildCost", delegate { return CostToken(module.buildCost); });
            return o;
        }

        // The recorded price of a module, which is what the decommission refund
        // and every other cost-return path reads. Null is a module that was never
        // given one: the engine's own founding path passes no cost either, so a
        // hab's core module reads null here in a vanilla game too.
        static JToken CostToken(TIResourcesCost cost)
        {
            if (cost == null) return JValue.CreateNull();
            var o = new JObject();
            Put(o, "completionTimeDays", delegate { return Num(cost.completionTime_days); });
            var resources = new JObject();
            List<ResourceValue> values = Safe<List<ResourceValue>>(
                delegate { return cost.resourceCosts; }, null);
            for (int i = 0; values != null && i < values.Count; i++)
                resources[values[i].resource.ToString()] = Num(values[i].value);
            o["resources"] = resources;
            return o;
        }

        #endregion

        #region spawn.module

        static JToken SpawnModule(JObject args)
        {
            TIHabState hab = Arg<TIHabState>(args, "hab");
            string name = Str(args, "module");
            if (string.IsNullOrEmpty(name)) throw new VerbError("missing arg 'module'");
            ModuleTemplate(name);
            bool complete = Bool(args, "complete", true);

            TIHabModuleState slot = Install(hab, name, complete);
            if (slot == null) throw new VerbError("no available slots");

            var o = new JObject();
            o["hab"] = Describe(hab);
            o["module"] = DescribeModule(slot);
            o["completed"] = complete;
            Put(o, "slotsRemaining", delegate
            {
                List<TIHabModuleState> left = hab.AvailableSlots();
                return (JToken)(left != null ? left.Count : 0);
            });
            return o;
        }

        // Installs one module in the hab's first available slot; null when there is
        // none. AvailableSlots reads the active sectors only and returns empty or
        // destroyed slots, so nothing standing is ever displaced.
        //
        // TIHabModuleState.SetModuleTemplate is private, and on its own it would leave
        // the construction flags, build dates, power status and build cost holding
        // whatever the slot carried before. InitiateConstructModule is both the public
        // entry point and the play path's own: it clears all of that first.
        static TIHabModuleState Install(TIHabState hab, string templateName, bool complete)
        {
            List<TIHabModuleState> slots = hab.AvailableSlots();
            if (slots == null || slots.Count == 0) return null;
            TIHabModuleState slot = slots[0];
            slot.InitiateConstructModule(templateName, BuildCost(hab, templateName),
                complete ? BuildNow : BuildNormally);
            if (complete) hab.CompleteModuleConstruction(slot);
            return slot;
        }

        // The price the hab's owner would have paid to build this module from
        // Earth, which is what the play path records: TIHabState.InitiateModule-
        // Construction hands TIHabModuleTemplate.CostFromEarth to
        // InitiateConstructModule, and that method's only use of a cost is
        // `if (cost != null) buildCost = new TIResourcesCost(cost)` -- charging is
        // a separate PayCost call the caller makes afterwards, which this fixture
        // still does not make. So the module carries its price and the faction is
        // not billed.
        //
        // A null cost here is what the verb used to pass always, and it leaves
        // buildCost null: the decommission refund reads that field through a null-
        // conditional, so every refund from a spawned module silently paid nothing.
        //
        // Best-effort: a cost that cannot be computed must not stop the module
        // from being installed, since a fixture with no price is still a fixture.
        static TIResourcesCost BuildCost(TIHabState hab, string templateName)
        {
            return Safe<TIResourcesCost>(delegate
            {
                TIFactionState faction = hab.faction;
                if (faction == null) return null;
                // isUpgrade false: AvailableSlots only ever returns an empty or
                // destroyed slot, so nothing is being upgraded.
                return ModuleTemplate(templateName).CostFromEarth(faction, hab, false);
            }, null);
        }

        static TIHabModuleTemplate ModuleTemplate(string name)
        {
            TIHabModuleTemplate template = TemplateManager.Find<TIHabModuleTemplate>(name, false);
            if (template == null)
                throw new VerbError("no TIHabModuleTemplate named '" + name + "'");
            return template;
        }

        static List<string> ModuleNames(JObject args, string key)
        {
            var names = new List<string>();
            JToken t = args != null ? args[key] : null;
            if (t == null || t.Type == JTokenType.Null) return names;
            var array = t as JArray;
            if (array == null)
                throw new VerbError("arg '" + key + "' must be an array of module data names");
            for (int i = 0; i < array.Count; i++)
            {
                string name = array[i] != null && array[i].Type != JTokenType.Null
                    ? array[i].ToString().Trim() : null;
                if (string.IsNullOrEmpty(name))
                    throw new VerbError("arg '" + key + "' holds an empty module name");
                names.Add(name);
            }
            if (names.Count > MaxSpawnModules)
                throw new VerbError("at most " + MaxSpawnModules + " modules per hab");
            return names;
        }

        #endregion

        #region spawn.army

        static JToken SpawnArmy(JObject args)
        {
            string type = Str(args, "type");
            if (string.IsNullOrEmpty(type)) type = "human";
            TIRegionState region = Arg<TIRegionState>(args, "region");

            if (Same(type, "human")) return SpawnHumanArmy(args, region);
            if (Same(type, "megafauna"))
            {
                // SpawnArmy hands the army to its home nation, which it reads through
                // the region. A region with no nation would throw inside the engine
                // with the army state already registered.
                if (Safe<TINationState>(delegate { return region.nation; }, null) == null)
                    throw new VerbError("region " + StateName(region)
                        + " has no nation; megafauna are added to the region's nation");
                var army = GameStateManager.CreateNewGameState<TIMegafaunaArmyState>();
                try { army.SpawnArmy(region); }
                catch (Exception) { DiscardState<TIMegafaunaArmyState>(army); throw; }
                return DescribeArmy(army, region, null);
            }
            if (Same(type, "invader"))
            {
                // No nation guard: this SpawnArmy also calls AddArmy, but on the alien
                // nation, and its one read of the region's nation is null-guarded.
                var army = GameStateManager.CreateNewGameState<TIAlienArmyState>();
                try { army.SpawnArmy(region); }
                catch (Exception) { DiscardState<TIAlienArmyState>(army); throw; }
                return DescribeArmy(army, region, null);
            }
            throw new VerbError("unknown army type '" + type
                + "' (human, megafauna, invader)");
        }

        // The nation's own army-build path, minus two of its steps: the new-army
        // notification, which is spam in a fixture, and the market adjustment, which is
        // an effect of the build priority rather than of an army existing.
        static JToken SpawnHumanArmy(JObject args, TIRegionState region)
        {
            TINationState nation = Arg<TINationState>(args, "nation");
            TINationState owner = Safe<TINationState>(
                delegate { return region.nation; }, null);
            if (!ReferenceEquals(owner, nation))
                throw new VerbError("region " + StateName(region) + " belongs to "
                    + (owner != null ? StateName(owner) : "no nation")
                    + ", not " + StateName(nation));

            float strength = Float(args, "strength", 1f);
            if (strength <= 0f) throw new VerbError("arg 'strength' must be > 0");

            var army = GameStateManager.CreateNewGameState<TIArmyState>();
            try
            {
                army.createdFromTemplate = false;
                army.deploymentType = DeploymentType.Standard;
                army.controlPointIdx = nation.GetNextArmyControlPointIdx();
                army.homeRegion = region;
                army.NewArmy(ArmyType.Human, 0, strength);
                army.MoveArmyToRegion(region, true);
                nation.AddArmy(army);
                nation.SetDataDirty();
                army.SetGameStateCreated();
            }
            catch (Exception)
            {
                DiscardState<TIArmyState>(army);
                throw;
            }

            return DescribeArmy(army, region, nation);
        }

        static JToken DescribeArmy(TIArmyState army, TIRegionState region, TINationState nation)
        {
            var o = new JObject();
            o["id"] = (int)army.ID;
            o["name"] = StateName(army);
            Put(o, "type", delegate { return (JToken)army.armyType.ToString(); });
            Put(o, "strength", delegate { return Num(army.strength); });
            o["region"] = Describe(region);
            o["nation"] = nation != null ? Describe(nation) : null;
            Put(o, "faction", delegate
            {
                TIFactionState faction = army.faction;
                return faction != null ? Describe(faction) : (JToken)JValue.CreateNull();
            });
            return o;
        }

        #endregion

        #region spawn.councilor

        // The template a fresh councilor is generated from. Every vanilla caller picks
        // one of these two by faction before generating.
        const string CouncilorTemplate = "randomizedCouncilor1";
        const string AlienCouncilorTemplate = "randomizedAlienCouncilor2";

        static JToken SpawnCouncilor(JObject args)
        {
            TIFactionState faction = Arg<TIFactionState>(args, "faction");
            // AddAvailableCouncilor adds to the council list unconditionally, so an
            // overfull council is only ever caught here.
            int empty = Safe<int>(delegate { return faction.emptyCouncilorSlots; }, 0);
            if (empty <= 0) throw new VerbError("council is full");

            TICouncilorTypeTemplate job = null;
            string jobName = Str(args, "job");
            if (!string.IsNullOrEmpty(jobName))
            {
                job = TemplateManager.Find<TICouncilorTypeTemplate>(jobName, false);
                if (job == null)
                    throw new VerbError("no TICouncilorTypeTemplate named '" + jobName + "'");
            }

            TIRegionState home = null;
            int regionId = OptionalInt(args, "region");
            if (regionId >= 0) home = ById<TIRegionState>(regionId);

            // NewCharacterGeneration's very first read is this.template.alien, and
            // CreateNewGameState leaves the template unset. Without this the call throws
            // with the councilor already registered, and the same read runs again from
            // the post-create init on every later save load, which breaks the save.
            // Resolved before anything is created, so a missing template costs nothing.
            string templateName = Safe<bool>(delegate { return faction.IsAlienFaction; }, false)
                ? AlienCouncilorTemplate : CouncilorTemplate;
            TICouncilorTemplate template =
                TemplateManager.Find<TICouncilorTemplate>(templateName, false);
            if (template == null)
                throw new VerbError("no TICouncilorTemplate named '" + templateName + "'");

            var councilor = GameStateManager.CreateNewGameState<TICouncilorState>();
            try
            {
                councilor.InitWithTemplate(template);
                councilor.NewCharacterGeneration(job, home, faction,
                    Flag(args, "max_stats"), false);
                // forced: the hire path (faction assignment, council list, recruit-pool
                // removal) without the recruitment cost or the hire milestones.
                faction.AddAvailableCouncilor(councilor, true);
            }
            catch (Exception)
            {
                DiscardState<TICouncilorState>(councilor);
                throw;
            }

            var o = new JObject();
            o["id"] = (int)councilor.ID;
            o["name"] = StateName(councilor);
            Put(o, "job", delegate
            {
                TICouncilorTypeTemplate t = councilor.typeTemplate;
                return t != null ? (JToken)new JValue(t.dataName) : JValue.CreateNull();
            });
            o["faction"] = Describe(faction);
            Put(o, "location", delegate
            {
                TIGameState at = councilor.location;
                return at != null ? Describe(at) : (JToken)JValue.CreateNull();
            });
            Put(o, "emptySlots", delegate { return (JToken)faction.emptyCouncilorSlots; });
            return o;
        }

        #endregion

        #region spawn.alien_site

        // Each region carries one holder state per alien site kind, all five created at
        // campaign init and hung on the region. This verb activates the holder that is
        // already there; creating another one would leave the region pointing at the old
        // one and the new state orphaned in the manager.
        static JToken SpawnAlienSite(JObject args)
        {
            TIRegionState region = Arg<TIRegionState>(args, "region");
            string kind = Str(args, "kind");
            if (string.IsNullOrEmpty(kind)) throw new VerbError("missing arg 'kind'");

            var o = new JObject();
            o["region"] = Describe(region);
            var state = new JObject();

            if (Same(kind, "facility"))
            {
                TIRegionAlienFacilityState holder = Holder<TIRegionAlienFacilityState>(
                    delegate { return region.alienFacility; }, "alien facility");
                if (Safe<bool>(delegate { return holder.built; }, false))
                    throw new VerbError("region already has an alien facility");
                holder.BuildFacility();
                o["holder"] = Describe(holder);
                Put(state, "built", delegate { return (JToken)holder.built; });
            }
            else if (Same(kind, "crashdown"))
            {
                TIRegionUFOCrashdownState holder = Holder<TIRegionUFOCrashdownState>(
                    delegate { return region.alienCrashdown; }, "alien crashdown");
                holder.TriggerCrashdown(Flag(args, "first"));
                o["holder"] = Describe(holder);
                Put(state, "crashdownPresent",
                    delegate { return (JToken)holder.crashdownPresent; });
            }
            else if (Same(kind, "landing"))
            {
                TIRegionUFOLandingState holder = Holder<TIRegionUFOLandingState>(
                    delegate { return region.alienLanding; }, "alien landing");
                // Each call adds another army-deployment listener to the same holder,
                // so triggering on a live landing deploys twice.
                if (Safe<bool>(delegate { return holder.landingPresent; }, false))
                    throw new VerbError("region already has an alien landing");
                // The engine's own default for "however long the template says" is a
                // negative override, so an absent 'days' passes straight through.
                holder.TriggerLanding(Float(args, "days", -1f));
                o["holder"] = Describe(holder);
                Put(state, "landingPresent",
                    delegate { return (JToken)holder.landingPresent; });
            }
            else if (Same(kind, "xenoforming"))
            {
                TIRegionXenoformingState holder = Holder<TIRegionXenoformingState>(
                    delegate { return region.xenoforming; }, "xenoforming");
                float level = RequiredFloat(args, "level");
                holder.SetXenoformingLevel(level);
                o["holder"] = Describe(holder);
                Put(state, "xenoformingLevel",
                    delegate { return Num(holder.xenoformingLevel); });
            }
            else
            {
                throw new VerbError("unknown alien site kind '" + kind
                    + "' (facility, crashdown, landing, xenoforming)");
            }

            o["kind"] = kind.ToLowerInvariant();
            o["state"] = state;
            return o;
        }

        static T Holder<T>(Func<T> read, string what) where T : TIGameState
        {
            T holder = Safe<T>(read, null);
            if (holder == null) throw new VerbError("region has no " + what + " state");
            return holder;
        }

        #endregion

        #region Shared helpers

        // Rollback for a state that was registered and then failed to initialize.
        // Archived without the event, as spawn.fleet does: the state was never
        // announced to anything, so nothing wants to hear it go.
        static void DiscardState<T>(T state) where T : TIGameState
        {
            if (state == null) return;
            try { state.ArchiveState(false); }
            catch (Exception) { }
            try { GameStateManager.RemoveGameState<T>(state.ID, false); }
            catch (Exception) { }
        }

        // Flag() answers false for an absent key. These verbs default to completing what
        // they spawn, so an absent key has to be able to mean true.
        static bool Bool(JObject args, string key, bool fallback)
        {
            JToken t = args != null ? args[key] : null;
            if (t == null || t.Type == JTokenType.Null) return fallback;
            return Flag(args, key);
        }

        // A present but non-numeric value is an error rather than a silent fallback:
        // a mistyped strength that quietly became 1.0 would look like a passing test.
        static float Float(JObject args, string key, float fallback)
        {
            JToken t = args != null ? args[key] : null;
            if (t == null || t.Type == JTokenType.Null) return fallback;
            try { return (float)t; }
            catch (Exception) { throw new VerbError("arg '" + key + "' must be a number"); }
        }

        static float RequiredFloat(JObject args, string key)
        {
            JToken t = args != null ? args[key] : null;
            if (t == null || t.Type == JTokenType.Null)
                throw new VerbError("missing arg '" + key + "'");
            return Float(args, key, 0f);
        }

        #endregion
    }
}

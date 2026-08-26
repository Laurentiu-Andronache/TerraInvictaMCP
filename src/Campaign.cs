using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using Newtonsoft.Json.Linq;
using PavonisInteractive.TerraInvicta;

namespace TerraInvictaMCP
{
    // campaign.new, prompts.list, prompts.dismiss.
    public static partial class Verbs
    {
        // The start screen offers four difficulties; the campaign records the index plus one.
        static readonly string[] DifficultyNames =
            new string[] { "Forgiving", "Normal", "Veteran", "Brutal" };
        const int DefaultDifficulty = 1;

        // The alert box restacks a new notification behind each press, so the loop that
        // drains it needs a bound.
        const int MaxAlertClicks = 64;

        static JToken CampaignNew(JObject args)
        {
            // The start screen scene is unloaded while a campaign runs, so its controller
            // would be missing anyway; the explicit refusal says why.
            if (HasCampaign) throw new VerbError("a campaign is loaded; return to the main menu first");
            StartMenuController menu = Require<StartMenuController>("start menu controller");
            if (menu.fatalStartupError) throw new VerbError("start menu reports a fatal startup error");
            RequireStartMenuReady(menu);

            // Order matters: a scenario change rebuilds the allowed-faction list and resets
            // the faction dropdown, so the scenario is settled before the faction is picked.
            string scenario = Str(args, "scenario");
            if (!string.IsNullOrEmpty(scenario))
                menu.UpdateStartOptions("Scenario", ScenarioTemplate(scenario));

            // The other option categories, written the same way the start screen's own
            // dropdowns write them. Still ahead of the faction pick: a FactionCouncils
            // option rebuilds the allowed-faction list and resets the dropdown too.
            ApplyStartOptions(menu, args);

            int difficulty = DifficultyIndex(args);
            menu.selectDifficultyDropdown.value = difficulty;
            menu.OnDifficultyChanged();

            TIFactionTemplate chosen = SelectFaction(menu, Str(args, "faction"));

            // The toggle is forced on for a player's first ever game, and the tutorial gates
            // the campaign behind scripted steps no client can answer.
            menu.tutorialToggle.isOn = false;
            menu.OnToggleTutorial();

            // The launch reads the dropdown, so the report is checked against the live
            // control rather than against what was asked for. Two option categories reset
            // that dropdown; this is the statement order above turned into a check.
            RequireFactionHolds(menu, chosen);

            // Built before the launch: the launch tears down this scene.
            var o = new JObject();
            o["scenario"] = ScenarioReport(menu);
            o["options"] = StartOptionsReport(menu);
            o["faction"] = FactionReport(chosen);
            o["difficulty"] = difficulty + 1;
            o["difficultyName"] = DifficultyNames[difficulty];
            o["tutorial"] = false;
            o["loading"] = true;

            // Same call the "start campaign" button makes: default campaign options, then
            // the scene load that builds the new gamestate.
            menu.OnLaunchLongCampaignClicked();
            return o;
        }

        static readonly FieldInfo startSceneManagerField = StartMenuField("sceneManager");
        static readonly FieldInfo allowedFactionsField = StartMenuField("currentAllowedFactions");
        static readonly FieldInfo startOptionsField = StartMenuField("currentStartOptions");

        static FieldInfo StartMenuField(string name)
        {
            try
            {
                return typeof(StartMenuController).GetField(name,
                    BindingFlags.NonPublic | BindingFlags.Instance);
            }
            catch (Exception) { return null; }
        }

        // The scene manager is resolved in the menu's Start. Without it the launch call
        // throws part way through, after it has already activated the loading screen.
        static void RequireStartMenuReady(StartMenuController menu)
        {
            if (startSceneManagerField == null)
                throw new VerbError("no sceneManager field on start menu");
            if (startSceneManagerField.GetValue(menu) == null)
                throw new VerbError("start menu is not initialized");
            // A scene load runs for tens of seconds and leaves no campaign behind until it
            // finishes, so no gamestate here can tell that a launch has already been issued.
            // Both routes out of this menu raise their own loading screen first, and that is
            // the flag: a second launch on top of the first reloads the scene mid-bootstrap.
            if (Launching(menu.loadingScreen) || Launching(LoadMenuScreen(menu)))
                throw new VerbError("a launch is already in progress");
        }

        static bool Launching(UnityEngine.GameObject screen)
        {
            return Safe<bool>(delegate { return screen != null && screen.activeSelf; }, false);
        }

        static UnityEngine.GameObject LoadMenuScreen(StartMenuController menu)
        {
            try
            {
                LoadMenuController loads = menu.loadMenuController;
                return loads != null ? loads.loadingScreen : null;
            }
            catch (Exception) { return null; }
        }

        static TIMetaTemplate ScenarioTemplate(string wanted)
        {
            var names = new List<string>();
            foreach (TIMetaTemplate t in TemplateManager.IterateByClass<TIMetaTemplate>(true))
            {
                if (t == null || !t.isNewCampaignOption) continue;
                if (!string.Equals(t.newCampaignOptionCategory, "Scenario", StringComparison.Ordinal)) continue;
                if (Same(t.dataName, wanted) || Same(ScenarioName(t), wanted)) return t;
                names.Add(t.dataName);
            }
            throw new VerbError("no scenario named '" + wanted + "' ("
                + string.Join(", ", names.ToArray()) + ")");
        }

        static string ScenarioName(TIMetaTemplate t)
        {
            return Safe<string>(delegate { return t.displayNameCurrentForStartScreen(); }, null);
        }

        static JToken ScenarioReport(StartMenuController menu)
        {
            TIMetaTemplate t = null;
            try { t = menu.GetSelectedScenarioMetaTemplate(); }
            catch (Exception) { }
            if (t == null) return JValue.CreateNull();
            var o = new JObject();
            o["dataName"] = t.dataName;
            o["name"] = ScenarioName(t);
            return o;
        }

        // 'options' is a list of TIMetaTemplate dataNames, one per category at most. Each
        // template names the category it belongs to, so the wire format carries names
        // alone and the write goes where the picker would have sent it.
        static void ApplyStartOptions(StartMenuController menu, JObject args)
        {
            List<string> wanted = OptionNames(args);
            var chosen = new List<TIMetaTemplate>();
            var taken = new Dictionary<string, string>(StringComparer.Ordinal);
            // Resolved in full before the first write, so a rejected list leaves the
            // option map as it was rather than half applied.
            for (int i = 0; i < wanted.Count; i++)
            {
                TIMetaTemplate t = OptionTemplate(wanted[i]);
                string category = t.newCampaignOptionCategory;
                string first;
                // The map keeps one entry per category, so a second option for a category
                // would silently discard the first.
                if (taken.TryGetValue(category, out first))
                    throw new VerbError("options set category '" + category + "' twice ('"
                        + first + "' then '" + t.dataName + "'); one option per category");
                taken[category] = t.dataName;
                chosen.Add(t);
            }
            for (int i = 0; i < chosen.Count; i++)
                menu.UpdateStartOptions(chosen[i].newCampaignOptionCategory, chosen[i]);
        }

        // A blank entry is a caller bug: dropping it would launch a default-option
        // campaign under a reply that says the option was honored.
        static List<string> OptionNames(JObject args)
        {
            var names = new List<string>();
            JToken t = args != null ? args["options"] : null;
            if (t == null || t.Type == JTokenType.Null) return names;
            var array = t as JArray;
            if (array == null)
            {
                names.Add(OptionName(t, 0));
                return names;
            }
            for (int i = 0; i < array.Count; i++) names.Add(OptionName(array[i], i));
            return names;
        }

        static string OptionName(JToken t, int index)
        {
            string name = t != null && t.Type != JTokenType.Null ? t.ToString().Trim() : null;
            if (string.IsNullOrEmpty(name))
                throw new VerbError("arg 'options' entry " + index + " is empty");
            return name;
        }

        // A start option is a meta template the picker offers outside the scenario
        // dropdown. Scenario is refused here: it resets the faction list, so the verb
        // settles it first from its own arg.
        static TIMetaTemplate OptionTemplate(string wanted)
        {
            var names = new List<string>();
            TIMetaTemplate found = null;
            foreach (TIMetaTemplate t in TemplateManager.IterateByClass<TIMetaTemplate>(true))
            {
                if (t == null) continue;
                if (Same(t.dataName, wanted)) { found = t; break; }
                if (SelectableOption(t)) names.Add(t.dataName);
            }
            if (found == null)
                throw new VerbError("no start option named '" + wanted + "' ("
                    + string.Join(", ", names.ToArray()) + ")");
            if (!found.isNewCampaignOption)
                throw new VerbError("meta template '" + found.dataName
                    + "' is not a new-campaign option");
            if (string.IsNullOrEmpty(found.newCampaignOptionCategory))
                throw new VerbError("new-campaign option '" + found.dataName
                    + "' has no option category");
            if (IsScenarioCategory(found.newCampaignOptionCategory))
                throw new VerbError("'" + found.dataName
                    + "' is a scenario; pass it as arg 'scenario'");
            return found;
        }

        static bool SelectableOption(TIMetaTemplate t)
        {
            return t.isNewCampaignOption
                && !string.IsNullOrEmpty(t.newCampaignOptionCategory)
                && !IsScenarioCategory(t.newCampaignOptionCategory);
        }

        static bool IsScenarioCategory(string category)
        {
            return string.Equals(category, "Scenario", StringComparison.Ordinal);
        }

        // The category->dataName map the launch reads back to load each option's
        // templates, so a caller can assert what the campaign was created with.
        static JToken StartOptionsReport(StartMenuController menu)
        {
            if (startOptionsField == null) return JValue.CreateNull();
            var options = Safe<Dictionary<string, string>>(
                delegate { return startOptionsField.GetValue(menu) as Dictionary<string, string>; },
                null);
            if (options == null) return JValue.CreateNull();
            var o = new JObject();
            foreach (KeyValuePair<string, string> pair in options)
            {
                if (pair.Key == null) continue;
                o[pair.Key] = pair.Value;
            }
            return o;
        }

        // Accepts the campaign's own 1..4 numbering or the start screen's label.
        static int DifficultyIndex(JObject args)
        {
            JToken t = args["difficulty"];
            if (t == null || t.Type == JTokenType.Null) return DefaultDifficulty;
            string s = t.ToString();
            int level;
            if (int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out level))
            {
                if (level < 1 || level > DifficultyNames.Length)
                    throw new VerbError("arg 'difficulty' must be 1.." + DifficultyNames.Length);
                return level - 1;
            }
            for (int i = 0; i < DifficultyNames.Length; i++)
            {
                if (Same(DifficultyNames[i], s)) return i;
            }
            throw new VerbError("unknown difficulty '" + s + "' ("
                + string.Join(", ", DifficultyNames) + ")");
        }

        // The dropdown index is the index into the allowed-faction list, and the menu's own
        // handler is what copies it into the scenario's active player faction.
        static TIFactionTemplate SelectFaction(StartMenuController menu, string wanted)
        {
            List<TIFactionTemplate> allowed = AllowedFactions(menu);
            int index = menu.newCampaignChooseFactionDropdown.value;
            if (!string.IsNullOrEmpty(wanted))
            {
                index = -1;
                var names = new List<string>();
                for (int i = 0; i < allowed.Count; i++)
                {
                    TIFactionTemplate f = allowed[i];
                    if (f == null) continue;
                    if (Same(f.dataName, wanted) || Same(FactionName(f), wanted)) { index = i; break; }
                    names.Add(f.dataName);
                }
                if (index < 0)
                    throw new VerbError("no faction named '" + wanted + "' ("
                        + string.Join(", ", names.ToArray()) + ")");
                menu.newCampaignChooseFactionDropdown.value = index;
                menu.OnFactionOptionSelected();
            }
            if (index < 0 || index >= allowed.Count) throw new VerbError("no faction selected");
            return allowed[index];
        }

        // Compared by dataName: a rebuilt allowed-faction list holds the same templates,
        // and what matters is which faction the dropdown index now names.
        static void RequireFactionHolds(StartMenuController menu, TIFactionTemplate chosen)
        {
            List<TIFactionTemplate> allowed = AllowedFactions(menu);
            int index = menu.newCampaignChooseFactionDropdown.value;
            TIFactionTemplate live = index >= 0 && index < allowed.Count ? allowed[index] : null;
            string now = live != null ? live.dataName : null;
            string want = chosen != null ? chosen.dataName : null;
            if (string.Equals(now, want, StringComparison.Ordinal)) return;
            throw new VerbError("the faction dropdown no longer selects '"
                + (want != null ? want : "none") + "' but '" + (now != null ? now : "none")
                + "'; a start screen control was set out of order");
        }

        static List<TIFactionTemplate> AllowedFactions(StartMenuController menu)
        {
            if (allowedFactionsField == null)
                throw new VerbError("no currentAllowedFactions field on start menu");
            var list = allowedFactionsField.GetValue(menu) as List<TIFactionTemplate>;
            if (list == null || list.Count == 0) throw new VerbError("start menu has no faction options");
            return list;
        }

        static string FactionName(TIFactionTemplate f)
        {
            return Safe<string>(delegate { return f.capitalizedFactionNameCurrent; }, null);
        }

        static JToken FactionReport(TIFactionTemplate f)
        {
            if (f == null) return JValue.CreateNull();
            var o = new JObject();
            o["dataName"] = f.dataName;
            o["name"] = FactionName(f);
            return o;
        }

        static TIPromptQueueState PromptQueue()
        {
            TIPromptQueueState queue = null;
            try { queue = GameStateManager.PromptQueue(); }
            catch (Exception) { }
            if (queue == null) throw new VerbError("no prompt queue");
            return queue;
        }

        static JToken PromptsList(JObject args)
        {
            TIPromptQueueState queue = PromptQueue();
            var o = new JObject();
            TIFactionState player = ActivePlayer();
            o["activePlayer"] = player != null ? Describe(player) : null;
            try { o["blocked"] = queue.anyActivePlayerBlocking; }
            catch (Exception) { o["blocked"] = null; }

            var a = new JArray();
            Append(a, FactionPrompts(queue), "faction");
            Append(a, NationPrompts(queue), "nation");
            o["prompts"] = a;
            return o;
        }

        static void Append(JArray a, List<Prompt> prompts, string scope)
        {
            for (int i = 0; i < prompts.Count; i++) a.Add(DescribePrompt(prompts[i], scope));
        }

        static JToken DescribePrompt(Prompt prompt, string scope)
        {
            var o = new JObject();
            o["name"] = prompt.name;
            o["scope"] = scope;
            try { o["value"] = prompt.value; } catch (Exception) { o["value"] = null; }
            o["handled"] = Handled(prompt.name);
            o["actingState"] = State(prompt.actingState);
            o["promptingState"] = State(prompt.promptingGameState);
            o["relatedState"] = State(prompt.relatedGameState);
            return o;
        }

        static JToken State(TIGameState state)
        {
            try { return state != null ? Describe(state) : JValue.CreateNull(); }
            catch (Exception) { return JValue.CreateNull(); }
        }

        // The neutral answers by prompt name, each the player action the game's own AI
        // path runs for an AI faction. A prompt type outside this table has no neutral
        // answer.
        static readonly Dictionary<string, Func<Prompt, TIFactionState, string>> answers =
            BuildAnswers();

        static Dictionary<string, Func<Prompt, TIFactionState, string>> BuildAnswers()
        {
            var t = new Dictionary<string, Func<Prompt, TIFactionState, string>>(StringComparer.Ordinal);
            t["PromptSelectTech"] = AnswerTech;
            t["PromptSelectProject"] = AnswerProject;
            t["PromptSelectCouncilorMissions"] = AnswerMissions;
            t["PromptDropOrgs"] = AnswerOrgSale;
            return t;
        }

        static bool Handled(string name)
        {
            return name != null && answers.ContainsKey(name);
        }

        static JToken PromptsDismiss(JObject args)
        {
            TIPromptQueueState queue = PromptQueue();
            string filter = Str(args, "type");
            bool all = Flag(args, "all");

            // A screen carries no prompt type, so a filtered call leaves the UI alone and
            // touches only the prompts it names.
            var screens = new JArray();
            if (string.IsNullOrEmpty(filter)) DismissScreens(screens);

            var dismissed = new JArray();
            var skipped = new JArray();
            List<Prompt> prompts = ActivePlayerPrompts(queue);
            for (int i = 0; i < prompts.Count; i++)
            {
                Prompt prompt = prompts[i];
                if (!string.IsNullOrEmpty(filter) && !Same(prompt.name, filter)) continue;
                string via = null;
                string reason = null;
                try { via = Answer(prompt); }
                catch (VerbError e) { reason = e.Message; }
                catch (Exception e) { reason = Note(e); }
                if (via != null)
                {
                    var o = new JObject();
                    o["name"] = prompt.name;
                    o["via"] = via;
                    dismissed.Add(o);
                }
                else
                {
                    var o = new JObject();
                    o["name"] = prompt.name;
                    o["reason"] = reason != null ? reason : "no neutral answer";
                    skipped.Add(o);
                }
            }

            // The escape hatch: drop what is left so the clock runs. The decision behind the
            // prompt goes unmade, which costs the player a research slot or a mission phase
            // and leaves the save intact.
            var forced = new JArray();
            if (all)
            {
                List<Prompt> left = ActivePlayerPrompts(queue);
                for (int i = 0; i < left.Count; i++)
                {
                    Prompt prompt = left[i];
                    if (!string.IsNullOrEmpty(filter) && !Same(prompt.name, filter)) continue;
                    bool removed = false;
                    try { removed = queue.RemovePrompt(prompt); }
                    catch (Exception) { }
                    if (!removed) continue;
                    var o = new JObject();
                    o["name"] = prompt.name;
                    forced.Add(o);
                }
            }

            var result = new JObject();
            result["screens"] = screens;
            result["dismissed"] = dismissed;
            result["skipped"] = skipped;
            result["forced"] = forced;
            result["remaining"] = ActivePlayerPrompts(queue).Count;
            try { result["blocked"] = queue.anyActivePlayerBlocking; }
            catch (Exception) { result["blocked"] = null; }
            return result;
        }

        static List<Prompt> ActivePlayerPrompts(TIPromptQueueState queue)
        {
            List<Prompt> all = FactionPrompts(queue);
            all.AddRange(NationPrompts(queue));
            return all;
        }

        // Copies: answering a prompt removes it from the live list mid-walk.
        static List<Prompt> FactionPrompts(TIPromptQueueState queue)
        {
            try
            {
                List<Prompt> list = queue.activePlayerFactionPromptList;
                return list != null ? new List<Prompt>(list) : new List<Prompt>();
            }
            catch (Exception) { return new List<Prompt>(); }
        }

        static List<Prompt> NationPrompts(TIPromptQueueState queue)
        {
            try
            {
                List<Prompt> list = queue.activePlayerNationPromptList;
                return list != null ? new List<Prompt>(list) : new List<Prompt>();
            }
            catch (Exception) { return new List<Prompt>(); }
        }

        // Returns null for a prompt with no answer in the table.
        static string Answer(Prompt prompt)
        {
            Func<Prompt, TIFactionState, string> answer;
            if (prompt.name == null || !answers.TryGetValue(prompt.name, out answer)) return null;
            return answer(prompt, PromptFaction(prompt));
        }

        static string AnswerTech(Prompt prompt, TIFactionState faction)
        {
            TITechTemplate tech = new PavonisInteractive.TerraInvicta.Tasks.StratTechSelector()
                .SelectTech(faction);
            if (tech == null) throw new VerbError("no tech to select");
            Runner(faction).StartAction(
                new PavonisInteractive.TerraInvicta.Actions.SelectTechAction(faction, prompt.value, tech));
            return "SelectTechAction " + tech.dataName;
        }

        static string AnswerProject(Prompt prompt, TIFactionState faction)
        {
            TIProjectTemplate project = new PavonisInteractive.TerraInvicta.Tasks.StratProjectSelector()
                .SelectProject(faction, prompt.value);
            if (project == null) throw new VerbError("no project to select");
            Runner(faction).StartAction(
                new PavonisInteractive.TerraInvicta.Actions.SelectProjectForDevelopmentAction(
                    faction, prompt.value, project));
            return "SelectProjectForDevelopmentAction " + project.dataName;
        }

        static string AnswerMissions(Prompt prompt, TIFactionState faction)
        {
            CouncilorMissionCanvasController missions =
                Require<CouncilorMissionCanvasController>("councilor mission controller");
            if (!Clickable(missions.confirmAssignmentsButton))
                throw new VerbError("mission assignments are not confirmable yet");
            missions.PlayerConfirmsMissionAssignments();
            return "PlayerConfirmsMissionAssignments";
        }

        static string AnswerOrgSale(Prompt prompt, TIFactionState faction)
        {
            TIOrgState org = SellableOrg(faction);
            if (org == null) throw new VerbError("faction has no sellable unassigned org");
            Runner(faction).StartAction(
                new PavonisInteractive.TerraInvicta.Actions.SellOrgAction(org, faction, null));
            return "SellOrgAction " + StateName(org);
        }

        static TIFactionState PromptFaction(Prompt prompt)
        {
            TIFactionState faction = null;
            try
            {
                TIGameState acting = prompt.actingState;
                if (acting != null) faction = acting.ref_faction;
            }
            catch (Exception) { }
            if (faction == null) throw new VerbError("prompt has no faction");
            return faction;
        }

        static PavonisInteractive.TerraInvicta.Entities.Player Runner(TIFactionState faction)
        {
            PavonisInteractive.TerraInvicta.Entities.Player player = null;
            try { player = faction.playerControl; }
            catch (Exception) { }
            if (player == null) throw new VerbError("faction has no player control");
            return player;
        }

        // Only a marketable org can be sold, which is the same filter the vanilla Autopilot
        // applies before it drops one.
        static TIOrgState SellableOrg(TIFactionState faction)
        {
            List<TIOrgState> orgs = null;
            try { orgs = faction.unassignedOrgs; }
            catch (Exception) { }
            if (orgs == null) return null;
            for (int i = 0; i < orgs.Count; i++)
            {
                TIOrgState org = orgs[i];
                if (org == null) continue;
                try
                {
                    TIOrgTemplate template = org.template;
                    if (template != null && template.allowedOnMarket) return org;
                }
                catch (Exception) { }
            }
            return null;
        }

        // A notification that carries a choice blocks until an option is taken, and which
        // option matters to a test: prompts.dismiss always takes the first live one. With
        // no arguments the verb reports the open alert's text and its live options; with
        // an option index it presses that button.
        static JToken AlertChoose(JObject args)
        {
            NotificationScreenController notices =
                Require<NotificationScreenController>("notification controller");

            bool open = false;
            try { open = notices.singleAlertBox != null && notices.singleAlertBox.activeInHierarchy; }
            catch (Exception) { }
            var o = new JObject();
            o["open"] = open;
            if (!open) return o;

            var texts = new JArray();
            try
            {
                var labels = notices.singleAlertBox.GetComponentsInChildren<TMPro.TMP_Text>(false);
                for (int i = 0; i < labels.Length && texts.Count < 8; i++)
                {
                    string s = labels[i] != null ? labels[i].text : null;
                    if (!string.IsNullOrEmpty(s)) texts.Add(new JValue(s));
                }
            }
            catch (Exception) { }
            o["text"] = texts;

            UnityEngine.UI.Button[] options = null;
            try { options = notices.optionButtons; }
            catch (Exception) { }
            var opts = new JArray();
            if (options != null)
            {
                for (int i = 0; i < options.Length; i++)
                {
                    if (!Clickable(options[i])) continue;
                    var row = new JObject();
                    row["option"] = i;
                    row["label"] = ButtonLabel(options[i]);
                    opts.Add(row);
                }
            }
            o["options"] = opts;

            int pick = OptionalInt(args, "option");
            if (pick >= 0)
            {
                if (options == null || pick >= options.Length || !Clickable(options[pick]))
                    throw new VerbError("option " + pick + " is not clickable");
                notices.OnOptionButtonPressed(pick);
                o["pressed"] = pick;
            }
            return o;
        }

        static string ButtonLabel(UnityEngine.UI.Button button)
        {
            try
            {
                var label = button.GetComponentInChildren<TMPro.TMP_Text>(false);
                return label != null ? label.text : null;
            }
            catch (Exception) { return null; }
        }

        // The modal screens the vanilla Autopilot clears on its way through a frame, each
        // taking the same decline-or-close branch it takes. The precombat screen is left
        // out: combat.autoresolve owns it and drives it a step per frame.
        static void DismissScreens(JArray log)
        {
            NotificationScreenController notices = Find<NotificationScreenController>();
            if (notices != null)
            {
                Alerts(notices, log);
                Screen(log, "policy.cancel", notices.masterPolicyPanelObject, delegate
                {
                    notices.PolicySelected(new CancelOption());
                    notices.OnConfirmPolicy();
                });
                Screen(log, "response.decline", notices.responsePanelObject, notices.OnResponseDecline);
                Screen(log, "warCall.decline", notices.callAllyResponseObject, notices.DeclineWarButton);
                Screen(log, "diplomacy.continue", Owner(notices.factionDiplomacyGreetingContinueButton),
                    notices.OnDiplomacyGreetingContinueButton);
                Screen(log, "diplomacy.close", Owner(notices.diplomacyController), notices.DiplomacyClose);
                Screen(log, "armies.goHome", notices.removeArmiesPromptObject,
                    notices.removeArmies_GoHomePressed);
            }

            ResearchScreenController research = Find<ResearchScreenController>();
            if (research != null)
                Screen(log, "research.close",
                    delegate { return research.Canvas != null && research.Canvas.enabled; },
                    delegate { research.CloseInfoScreen(false); });

            OperationCanvasController operations =
                Safe<OperationCanvasController>(delegate { return OperationCanvasController.Singleton; }, null);
            if (operations != null)
                Screen(log, "trajectory.cancel",
                    delegate { return Clickable(operations.changeTrajectoryCancelButton); },
                    operations.OnCancelTrajectoryChange);
        }

        // One dismissable screen: when ready reports it up, press is run and the label
        // logged. A throw in either leaves the rest of the pass untouched.
        static void Screen(JArray log, string label, Func<bool> ready, Action press)
        {
            try
            {
                if (!ready()) return;
                press();
                log.Add(new JValue(label));
            }
            catch (Exception) { }
        }

        static void Screen(JArray log, string label, UnityEngine.GameObject panel, Action press)
        {
            Screen(log, label,
                delegate { return panel != null && panel.activeInHierarchy; }, press);
        }

        static UnityEngine.GameObject Owner(UnityEngine.Component component)
        {
            return Safe<UnityEngine.GameObject>(
                delegate { return component != null ? component.gameObject : null; }, null);
        }

        // The alert box restacks: each press can reveal the next queued notification, so the
        // press order the Autopilot uses is repeated until nothing is left to click.
        static void Alerts(NotificationScreenController notices, JArray log)
        {
            try
            {
                if (notices.singleAlertBox == null || !notices.singleAlertBox.activeInHierarchy) return;
                int clicks = 0;
                while (clicks < MaxAlertClicks)
                {
                    if (Clickable(notices.okayButton)) notices.OkayButtonPressed();
                    else if (Clickable(notices.closeButton)) notices.CloseButtonPressed();
                    else if (Clickable(notices.gotoButton)) notices.GotoButtonPressed();
                    else break;
                    clicks++;
                }
                if (clicks > 0) log.Add(new JValue("alert.close x" + clicks));

                // A notification carrying a choice offers option buttons instead; the first
                // live one is taken, as the Autopilot does.
                UnityEngine.UI.Button[] options = notices.optionButtons;
                if (options == null) return;
                for (int i = 0; i < options.Length; i++)
                {
                    if (!Clickable(options[i])) continue;
                    notices.OnOptionButtonPressed(i);
                    log.Add(new JValue("alert.option " + i));
                    return;
                }
            }
            catch (Exception) { }
        }

        static bool Flag(JObject args, string key)
        {
            JToken t = args != null ? args[key] : null;
            if (t == null || t.Type == JTokenType.Null) return false;
            if (t.Type == JTokenType.Boolean) return (bool)t;
            string s = t.ToString();
            return string.Equals(s, "true", StringComparison.OrdinalIgnoreCase);
        }
    }
}

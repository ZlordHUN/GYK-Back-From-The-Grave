using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using Newtonsoft.Json;
using UnityEngine;

namespace GraveyardKeeperCoop.Multiplayer
{
#pragma warning disable 0649 // JSON deserialization populates these DTO fields through reflection.
    [Serializable]
    internal sealed class StoryCatalogFile
    {
        public int schemaVersion;
        public string catalogId;
        public string campaign;
        public string chapter;
        public StoryCatalogQuest[] quests;
        public StoryCatalogTaskMilestone[] taskMilestones;
        public StoryCatalogCutscene[] cutscenes;
        public StoryCatalogStep[] storySteps;
    }

    [Serializable]
    internal sealed class StoryCatalogQuest
    {
        public string questId;
        public string title;
        public StoryCatalogQuestDefinition vanilla;
        public StoryCatalogOwnership policy;
        public StoryCatalogStateCheck[] completionChecks;

        [NonSerialized]
        public string catalogId;
    }

    [Serializable]
    internal sealed class StoryCatalogQuestDefinition
    {
        public string startTrigger;
        public string successTrigger;
        public string failTrigger;
        public string[] startKeys;
        public bool questVisible;
        public bool oneTimeQuest;
        public string startScript;
        public string successScript;
        public string failScript;
        public string arrowWgoCustomTag;
        public string arrowWgoObjectId;
        public StoryCatalogReward[] successRewards;
        public StoryCatalogReward[] failureRewards;
    }

    [Serializable]
    internal sealed class StoryCatalogReward
    {
        public string type;
        public float amount;
    }

    [Serializable]
    internal sealed class StoryCatalogOwnership
    {
        public string stateOwnership;
        public string eligibleInitiator;
        public string livePresentationOwner;
        public string observerQuestApplication;
        public string personalRewardDelivery;
        public StoryCatalogPersonalRewardSource personalRewardSource;
        public StoryCatalogObserverSuccess observerStart;
        public StoryCatalogObserverSuccess observerSuccess;
    }

    [Serializable]
    internal sealed class StoryCatalogPersonalRewardSource
    {
        public string containerCustomTag;
        public string conversionMode;
        public string conversionMarker;
    }

    [Serializable]
    internal sealed class StoryCatalogObserverSuccess
    {
        public string[] globalScripts;
        public StoryCatalogPlayerParamRepair[] playerParamRepairs;
        public StoryCatalogPlayerParamRepair[] playerParamMinimums;
        public StoryCatalogTaskStateRepair[] taskStateMinimums;
        public string[] unlockedTechs;
        public string[] unlockedPhrases;
        public string[] blacklistedPhrases;
        public string[] unlockedCrafts;
        public string[] completedOneTimeCrafts;
        public StoryCatalogPersonalItemGrant[] personalItemGrants;
    }

    [Serializable]
    internal sealed class StoryCatalogPlayerParamRepair
    {
        public string key;
        public float value;
        public string[] unlessQuestSucceeded;
        public string[] unlessQuestReached;
    }

    [Serializable]
    internal sealed class StoryCatalogTaskStateRepair
    {
        public string npcId;
        public string taskId;
        public string state;
    }

    [Serializable]
    internal sealed class StoryCatalogWgoParamRepair
    {
        public string objectId;
        public string key;
        public float value;
    }

    [Serializable]
    internal sealed class StoryCatalogPersonalItemGrant
    {
        public string itemId;
        public int count;
        public string receiptId;
    }

    [Serializable]
    internal sealed class StoryCatalogStateCheck
    {
        public string source;
        public string key;
        public string comparison;
        public float value;
    }

    [Serializable]
    internal sealed class StoryCatalogTaskMilestone
    {
        public string milestoneId;
        public string npcId;
        public string taskId;
        public string[] activeStates;
        public StoryCatalogPlayerParamRepair[] playerParamRepairs;
        public StoryCatalogPlayerParamRepair[] playerParamMinimums;
        public StoryCatalogTaskStateRepair[] taskStateMinimums;
        public string[] unlockedTechs;
        public string[] unlockedPhrases;
        public string[] blacklistedPhrases;
        public string[] unlockedCrafts;
        public string[] completedOneTimeCrafts;
        public StoryCatalogWgoParamRepair[] wgoParamMinimums;
        public StoryCatalogPersonalItemGrant[] personalItemGrants;

        [NonSerialized]
        public string catalogId;
    }

    [Serializable]
    internal sealed class StoryCatalogCutscene
    {
        public string cutsceneId;
        public string[] linkedQuestIds;
        public string[] triggerScripts;
        public string[] actors;
        public string observerPolicy;
        public string lateJoinPolicy;
        public string remoteExecutionPolicy;
        public string executionOwnerPolicy;
        public string dialogueOwnerPolicy;
        public string formationPolicy;
        public string participantFacing;
        public string[] personalGrantPrerequisiteQuestIds;
        public StoryCatalogPersonalItemGrant[] personalItemGrants;
        public StoryCatalogStateCheck[] completionChecks;
        public string[] cleanupRequirements;

        [NonSerialized]
        public string catalogId;
    }

    [Serializable]
    internal sealed class StoryCatalogStep
    {
        public string stepId;
        public string title;
        public string[] linkedQuestIds;
        public string[] linkedTaskMilestoneIds;
        public string[] triggerScripts;
        public string[] triggerKeys;
        public StoryCatalogExpectedEffect[] expectedEffects;
        public string[] safeguards;

        [NonSerialized]
        public string catalogId;
    }

    [Serializable]
    internal sealed class StoryCatalogExpectedEffect
    {
        public string kind;
        public string id;
        public string operation;
        public float amount;
        public string ownership;
    }
#pragma warning restore 0649

    /// <summary>
    /// Loads the machine-readable story policy embedded in the mod, verifies
    /// catalogued quests against the game's live balance data, and supplies policy
    /// decisions to the existing quest and cutscene synchronization engines. This
    /// layer is policy-only: callers execute the validated side effects.
    /// </summary>
    internal static class StoryCatalogRuntime
    {
        private const int SupportedSchemaVersion = 5;
        private const string ResourceMarker = ".StoryCatalog.";
        private const string DurableStateOnly = "durable_state_only";
        private const string LocalMirrorRequired = "local_mirror_required";
        private const string LiveObservation = "live_observation";
        private const string LobbyHost = "lobby_host";
        private const string InitiatingPlayer = "initiating_player";
        private const string PersonalGrantReceiptPrefix =
            "gykmp_story_grant_";
        private const string PersonalRewardSourceMarkerPrefix =
            "gykmp_story_source_";
        internal const string ConsumeExactUntouchedSeed =
            "consume_exact_untouched_seed";
        internal const string LegacyReceiptAdoptionId =
            "gykmp_story_grant_receipts_migrated_v1";

        private sealed class ScriptBinding
        {
            public readonly List<string> QuestIds = new List<string>();
            public readonly List<string> CutsceneIds = new List<string>();
            public readonly List<string> StepIds = new List<string>();
        }

        private static readonly Dictionary<string, StoryCatalogQuest> Quests =
            new Dictionary<string, StoryCatalogQuest>(StringComparer.Ordinal);
        private static readonly Dictionary<string, StoryCatalogTaskMilestone> TaskMilestones =
            new Dictionary<string, StoryCatalogTaskMilestone>(StringComparer.Ordinal);
        private static readonly Dictionary<string, StoryCatalogCutscene> Cutscenes =
            new Dictionary<string, StoryCatalogCutscene>(StringComparer.Ordinal);
        private static readonly Dictionary<string, StoryCatalogStep> StorySteps =
            new Dictionary<string, StoryCatalogStep>(StringComparer.Ordinal);
        private static readonly Dictionary<string, ScriptBinding> Scripts =
            new Dictionary<string, ScriptBinding>(StringComparer.OrdinalIgnoreCase);
        private static readonly HashSet<string> CatalogIds =
            new HashSet<string>(StringComparer.Ordinal);
        private static readonly HashSet<string> ValidatedQuests =
            new HashSet<string>(StringComparer.Ordinal);
        private static readonly HashSet<string> InvalidQuestDefinitions =
            new HashSet<string>(StringComparer.Ordinal);
        private static readonly HashSet<string> LoggedEvents =
            new HashSet<string>(StringComparer.Ordinal);
        private static readonly HashSet<string> PersonalGrantReceiptIds =
            new HashSet<string>(StringComparer.Ordinal)
            {
                LegacyReceiptAdoptionId
            };

        private static bool initialized;

        public static void Initialize()
        {
            if (initialized)
                return;

            initialized = true;
            string[] resources = Assembly.GetExecutingAssembly().GetManifestResourceNames();
            Array.Sort(resources, StringComparer.Ordinal);

            int candidateFiles = 0;
            int loadedFiles = 0;
            for (int i = 0; i < resources.Length; i++)
            {
                string resourceName = resources[i];
                if (resourceName.IndexOf(ResourceMarker, StringComparison.Ordinal) < 0 ||
                    !resourceName.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                candidateFiles++;
                try
                {
                    LoadResource(resourceName);
                    loadedFiles++;
                }
                catch (Exception ex)
                {
                    CoopMod.Logger.LogError(
                        $"[StoryCatalog] Failed to load '{resourceName}': {ex.Message}");
                }
            }

            ValidateLinks();
            if (loadedFiles == 0)
            {
                CoopMod.Logger.LogError(
                    candidateFiles == 0
                        ? "[StoryCatalog] No embedded catalog resources were found"
                        : $"[StoryCatalog] None of the {candidateFiles} embedded catalog " +
                          "resource(s) loaded successfully");
                return;
            }

            CoopMod.Logger.LogInfo(
                $"[StoryCatalog] Loaded {Quests.Count} quests, " +
                $"{TaskMilestones.Count} task milestones, and {Cutscenes.Count} " +
                $"cutscenes across {StorySteps.Count} story steps from " +
                $"{loadedFiles} catalog file(s)");
        }

        public static void ResetSession()
        {
            ValidatedQuests.Clear();
            InvalidQuestDefinitions.Clear();
            LoggedEvents.Clear();
        }

        public static void ValidateAllQuestDefinitions()
        {
            EnsureInitialized();
            GameBalance balance = GameBalance.me;
            if (balance == null)
            {
                CoopMod.Logger.LogError(
                    "[StoryCatalog] Cannot validate the catalog because GameBalance " +
                    "is unavailable");
                return;
            }

            int missing = 0;
            int mismatched = 0;
            foreach (KeyValuePair<string, StoryCatalogQuest> pair in Quests)
            {
                QuestDefinition definition =
                    balance.GetDataOrNull<QuestDefinition>(pair.Key);
                if (definition == null)
                {
                    missing++;
                    CoopMod.Logger.LogError(
                        $"[StoryCatalog] Catalogued QuestDefinition '{pair.Key}' " +
                        "is missing from the live game balance");
                    continue;
                }

                if (!ValidateQuestDefinition(pair.Value, definition))
                    mismatched++;
            }

            if (missing == 0 && mismatched == 0)
            {
                CoopMod.Logger.LogInfo(
                    $"[StoryCatalog] Validated all {Quests.Count} catalogued " +
                    "QuestDefinition records against the live game balance");
                return;
            }

            CoopMod.Logger.LogError(
                $"[StoryCatalog] Live definition audit failed: total={Quests.Count}, " +
                $"missing={missing}, mismatched={mismatched}");
        }

        public static void ObserveQuestStarted(QuestDefinition definition, bool remote)
        {
            ObserveQuest(definition, "started", remote, false);
        }

        public static void ObserveQuestEnded(
            QuestDefinition definition,
            bool succeeded,
            bool remote)
        {
            ObserveQuest(
                definition,
                succeeded ? "succeeded" : "failed",
                remote,
                succeeded);
        }

        public static void ObserveFlowScriptStarted(
            string scriptName,
            string attachment,
            bool remote)
        {
            EnsureInitialized();
            if (string.IsNullOrEmpty(scriptName) ||
                !Scripts.TryGetValue(scriptName, out ScriptBinding binding))
            {
                return;
            }

            string origin = remote ? "remote-presentation" : "local";
            string eventKey = $"script:{origin}:{attachment}:{scriptName}";
            if (!LoggedEvents.Add(eventKey))
                return;

            CoopMod.Logger.LogInfo(
                $"[StoryCatalog] FlowScript '{scriptName}' observed " +
                $"({origin}, {attachment}); quests=[{Join(binding.QuestIds)}], " +
                $"cutscenes=[{Join(binding.CutsceneIds)}], " +
                $"steps=[{Join(binding.StepIds)}]");
        }

        public static string[] GetObserverSuccessScripts(string questId)
        {
            StoryCatalogObserverSuccess success = GetObserverSuccess(questId);
            return success?.globalScripts ?? new string[0];
        }

        public static StoryCatalogObserverSuccess GetObserverStartEffects(
            string questId)
        {
            return GetObserverEffects(questId, success: false);
        }

        public static StoryCatalogObserverSuccess GetObserverSuccessEffects(
            string questId)
        {
            return GetObserverSuccess(questId);
        }

        public static StoryCatalogPlayerParamRepair[] GetObserverSuccessPlayerParamRepairs(
            string questId)
        {
            StoryCatalogObserverSuccess success = GetObserverSuccess(questId);
            return success?.playerParamRepairs ?? new StoryCatalogPlayerParamRepair[0];
        }

        public static StoryCatalogPersonalItemGrant[] GetObserverSuccessPersonalItemGrants(
            string questId)
        {
            StoryCatalogObserverSuccess success = GetObserverSuccess(questId);
            return success?.personalItemGrants ??
                new StoryCatalogPersonalItemGrant[0];
        }

        public static StoryCatalogPersonalItemGrant[] GetCutscenePersonalItemGrants(
            string scriptName)
        {
            StoryCatalogCutscene cutscene = GetCutscenePolicy(scriptName);
            return cutscene?.personalItemGrants ??
                new StoryCatalogPersonalItemGrant[0];
        }

        public static StoryCatalogCutscene[] GetCutscenesWithPersonalItemGrants()
        {
            EnsureInitialized();
            var cutscenes = new List<StoryCatalogCutscene>();
            foreach (StoryCatalogCutscene cutscene in Cutscenes.Values)
            {
                if ((cutscene?.personalItemGrants?.Length ?? 0) > 0)
                    cutscenes.Add(cutscene);
            }
            cutscenes.Sort((left, right) => string.CompareOrdinal(
                left.cutsceneId,
                right.cutsceneId));
            return cutscenes.ToArray();
        }

        public static StoryCatalogPersonalRewardSource GetPersonalRewardSource(
            string questId)
        {
            EnsureInitialized();
            if (string.IsNullOrEmpty(questId) ||
                !Quests.TryGetValue(questId, out StoryCatalogQuest quest))
            {
                return null;
            }

            return quest.policy?.personalRewardSource;
        }

        public static bool TryGetPersonalRewardSourceForContainer(
            string containerCustomTag,
            out string questId,
            out StoryCatalogPersonalRewardSource source,
            out StoryCatalogPersonalItemGrant[] grants)
        {
            EnsureInitialized();
            questId = string.Empty;
            source = null;
            grants = new StoryCatalogPersonalItemGrant[0];
            if (string.IsNullOrEmpty(containerCustomTag))
                return false;

            foreach (KeyValuePair<string, StoryCatalogQuest> pair in Quests)
            {
                StoryCatalogPersonalRewardSource candidate =
                    pair.Value.policy?.personalRewardSource;
                if (candidate == null ||
                    !string.Equals(
                        candidate.containerCustomTag,
                        containerCustomTag,
                        StringComparison.Ordinal))
                {
                    continue;
                }

                questId = pair.Key;
                source = candidate;
                grants = pair.Value.policy?.observerSuccess?.personalItemGrants ??
                    new StoryCatalogPersonalItemGrant[0];
                return true;
            }

            return false;
        }

        public static string[] GetQuestIdsWithObserverPlayerParamRepairs()
        {
            EnsureInitialized();
            var questIds = new List<string>();
            foreach (KeyValuePair<string, StoryCatalogQuest> pair in Quests)
            {
                StoryCatalogPlayerParamRepair[] repairs =
                    pair.Value.policy?.observerSuccess?.playerParamRepairs;
                if (repairs != null && repairs.Length > 0)
                    questIds.Add(pair.Key);
            }
            questIds.Sort(StringComparer.Ordinal);
            return questIds.ToArray();
        }

        public static string[] GetQuestIdsWithObserverSuccessEffects()
        {
            EnsureInitialized();
            var questIds = new List<string>();
            foreach (KeyValuePair<string, StoryCatalogQuest> pair in Quests)
            {
                StoryCatalogObserverSuccess success =
                    pair.Value.policy?.observerSuccess;
                if (HasDurableEffects(success))
                    questIds.Add(pair.Key);
            }
            questIds.Sort(StringComparer.Ordinal);
            return questIds.ToArray();
        }

        public static string[] GetQuestIdsWithObserverStartEffects()
        {
            EnsureInitialized();
            var questIds = new List<string>();
            foreach (KeyValuePair<string, StoryCatalogQuest> pair in Quests)
            {
                if (HasDurableEffects(pair.Value.policy?.observerStart))
                    questIds.Add(pair.Key);
            }
            questIds.Sort(StringComparer.Ordinal);
            return questIds.ToArray();
        }

        public static string[] GetPersonalGrantReceiptIds()
        {
            EnsureInitialized();
            var receiptIds = new List<string>(PersonalGrantReceiptIds);
            receiptIds.Sort(StringComparer.Ordinal);
            return receiptIds.ToArray();
        }

        public static bool IsPersonalGrantReceiptId(string receiptId)
        {
            EnsureInitialized();
            return !string.IsNullOrEmpty(receiptId) &&
                   PersonalGrantReceiptIds.Contains(receiptId);
        }

        internal static bool IsKnownQuestId(string questId)
        {
            EnsureInitialized();
            return !string.IsNullOrEmpty(questId) && Quests.ContainsKey(questId);
        }

        public static StoryCatalogTaskMilestone[] GetTaskMilestones()
        {
            EnsureInitialized();
            var milestones = new List<StoryCatalogTaskMilestone>(TaskMilestones.Values);
            milestones.Sort((left, right) => string.CompareOrdinal(
                left.milestoneId,
                right.milestoneId));
            return milestones.ToArray();
        }

        public static bool IsTaskMilestoneActive(
            StoryCatalogTaskMilestone milestone,
            KnownNPC.TaskState.State state)
        {
            if (milestone?.activeStates == null)
                return false;

            string stateName = state.ToString();
            for (int i = 0; i < milestone.activeStates.Length; i++)
            {
                if (string.Equals(
                        milestone.activeStates[i],
                        stateName,
                        StringComparison.Ordinal))
                {
                    return true;
                }
            }
            return false;
        }

        public static string ResolveKnownNpcId(string objectId)
        {
            if (string.IsNullOrEmpty(objectId))
                return string.Empty;

            ObjectDefinition definition =
                GameBalance.me?.GetDataOrNull<ObjectDefinition>(objectId);
            return definition != null && !string.IsNullOrEmpty(definition.npc_alias)
                ? definition.npc_alias
                : objectId;
        }

        /// <summary>
        /// Catalogued quests explicitly choose how observers apply remote quest
        /// transitions. Uncatalogued quests retain the established state-only path.
        /// </summary>
        public static bool ShouldApplyRemoteQuestAsDurableStateOnly(string questId)
        {
            EnsureInitialized();
            if (string.IsNullOrEmpty(questId) ||
                !Quests.TryGetValue(questId, out StoryCatalogQuest quest))
            {
                return true;
            }

            bool durableStateOnly = string.Equals(
                quest.policy.observerQuestApplication,
                DurableStateOnly,
                StringComparison.Ordinal);
            if (!durableStateOnly &&
                LoggedEvents.Add($"unsupported-observer-policy:{questId}"))
            {
                CoopMod.Logger.LogError(
                    $"[StoryCatalog] Quest '{questId}' requests unsupported observer " +
                    $"application '{quest.policy.observerQuestApplication}'");
            }
            return durableStateOnly;
        }

        public static bool RequiresLocalFlowScriptMirror(string scriptName)
        {
            StoryCatalogCutscene cutscene = GetCutscenePolicy(scriptName);
            return cutscene != null && string.Equals(
                cutscene.remoteExecutionPolicy,
                LocalMirrorRequired,
                StringComparison.Ordinal);
        }

        public static bool RequiresLobbyHostDialogueOwner(string scriptName)
        {
            StoryCatalogCutscene cutscene = GetCutscenePolicy(scriptName);
            return cutscene != null && string.Equals(
                cutscene.dialogueOwnerPolicy,
                LobbyHost,
                StringComparison.Ordinal);
        }

        public static bool RequiresLobbyHostExecution(string scriptName)
        {
            StoryCatalogCutscene cutscene = GetCutscenePolicy(scriptName);
            return cutscene != null && string.Equals(
                cutscene.executionOwnerPolicy,
                LobbyHost,
                StringComparison.Ordinal);
        }

        public static bool TryGetParticipantFacing(
            string scriptName,
            out Direction facing)
        {
            facing = Direction.Down;
            StoryCatalogCutscene cutscene = GetCutscenePolicy(scriptName);
            return cutscene != null &&
                !string.IsNullOrEmpty(cutscene.participantFacing) &&
                Enum.TryParse(cutscene.participantFacing, true, out facing) &&
                (byte)facing >= (byte)Direction.Right &&
                (byte)facing <= (byte)Direction.Down;
        }

        public static bool UsesFormationPolicy(
            string scriptName,
            string formationPolicy)
        {
            StoryCatalogCutscene cutscene = GetCutscenePolicy(scriptName);
            return cutscene != null &&
                !string.IsNullOrEmpty(formationPolicy) &&
                string.Equals(
                    cutscene.formationPolicy,
                    formationPolicy,
                    StringComparison.Ordinal);
        }

        private static void ObserveQuest(
            QuestDefinition definition,
            string transition,
            bool remote,
            bool validateCompletion)
        {
            EnsureInitialized();
            if (definition == null || string.IsNullOrEmpty(definition.id) ||
                !Quests.TryGetValue(definition.id, out StoryCatalogQuest record))
            {
                return;
            }

            ValidateQuestDefinition(record, definition);
            string origin = remote ? "remote-state" : "local";
            string eventKey = $"quest:{origin}:{transition}:{definition.id}";
            if (LoggedEvents.Add(eventKey))
            {
                CoopMod.Logger.LogInfo(
                    $"[StoryCatalog] Quest '{definition.id}' {transition} ({origin}); " +
                    $"state={record.policy.stateOwnership}, " +
                    $"initiator={record.policy.eligibleInitiator}, " +
                    $"presentation={record.policy.livePresentationOwner}, " +
                    $"observer={record.policy.observerQuestApplication}");
            }

            if (validateCompletion && !remote)
                ValidateCompletionChecks(record);
        }

        private static void LoadResource(string resourceName)
        {
            string json;
            using (Stream stream = Assembly.GetExecutingAssembly()
                       .GetManifestResourceStream(resourceName))
            {
                if (stream == null)
                    throw new InvalidDataException("resource stream is missing");

                using (var reader = new StreamReader(stream))
                    json = reader.ReadToEnd();
            }

            // Unity's JsonUtility silently discarded the entire quests array once
            // the catalog gained deeper observer-success and story-step records.
            // Graveyard Keeper already ships Newtonsoft.Json, whose parser handles
            // the complete schema and reports malformed input instead of returning
            // a partially empty object.
            StoryCatalogFile file = JsonConvert.DeserializeObject<StoryCatalogFile>(json);
            ValidateFile(file);
            RegisterFile(file);
        }

        private static void ValidateFile(StoryCatalogFile file)
        {
            if (file == null)
                throw new InvalidDataException("document is empty");
            if (file.schemaVersion != SupportedSchemaVersion)
            {
                throw new InvalidDataException(
                    $"unsupported schema version {file.schemaVersion}");
            }
            if (string.IsNullOrWhiteSpace(file.catalogId) ||
                string.IsNullOrWhiteSpace(file.campaign) ||
                string.IsNullOrWhiteSpace(file.chapter))
            {
                throw new InvalidDataException(
                    "catalogId, campaign, and chapter are required");
            }
            if (CatalogIds.Contains(file.catalogId))
                throw new InvalidDataException($"duplicate catalogId '{file.catalogId}'");
            if (file.quests == null || file.quests.Length == 0)
                throw new InvalidDataException("at least one quest record is required");

            var fileReceiptIds = new HashSet<string>(StringComparer.Ordinal);
            var fileRewardSourceTags = new HashSet<string>(StringComparer.Ordinal);
            for (int i = 0; i < file.quests.Length; i++)
            {
                StoryCatalogQuest quest = file.quests[i];
                if (quest == null || string.IsNullOrWhiteSpace(quest.questId))
                    throw new InvalidDataException($"quest #{i} has no questId");
                if (quest.vanilla == null || quest.policy == null)
                    throw new InvalidDataException(
                        $"quest '{quest.questId}' requires vanilla and policy records");
                if (!string.Equals(
                        quest.policy.stateOwnership,
                        "shared_campaign",
                        StringComparison.Ordinal))
                {
                    throw new InvalidDataException(
                        $"quest '{quest.questId}' must use shared_campaign state ownership");
                }
                if (string.IsNullOrWhiteSpace(quest.policy.eligibleInitiator) ||
                    string.IsNullOrWhiteSpace(quest.policy.livePresentationOwner) ||
                    string.IsNullOrWhiteSpace(quest.policy.observerQuestApplication) ||
                    string.IsNullOrWhiteSpace(quest.policy.personalRewardDelivery))
                {
                    throw new InvalidDataException(
                        $"quest '{quest.questId}' has an incomplete ownership policy");
                }
                if (!string.Equals(
                        quest.policy.observerQuestApplication,
                        DurableStateOnly,
                        StringComparison.Ordinal))
                {
                    throw new InvalidDataException(
                        $"quest '{quest.questId}' uses unsupported observer application " +
                        $"'{quest.policy.observerQuestApplication}'");
                }

                ValidateObserverEffects(
                    quest.policy.observerStart,
                    $"quest '{quest.questId}' observer start",
                    fileReceiptIds);
                if ((quest.policy.observerStart?.globalScripts?.Length ?? 0) > 0 ||
                    (quest.policy.observerStart?.personalItemGrants?.Length ?? 0) > 0)
                {
                    throw new InvalidDataException(
                        $"quest '{quest.questId}' observer start cannot execute " +
                        "scripts or personal item grants");
                }
                ValidateObserverEffects(
                    quest.policy.observerSuccess,
                    $"quest '{quest.questId}' observer success",
                    fileReceiptIds);

                StoryCatalogPersonalRewardSource rewardSource =
                    quest.policy.personalRewardSource;
                if (rewardSource != null)
                {
                    if (string.IsNullOrWhiteSpace(rewardSource.containerCustomTag) ||
                        string.IsNullOrWhiteSpace(rewardSource.conversionMarker) ||
                        !string.Equals(
                            rewardSource.conversionMode,
                            ConsumeExactUntouchedSeed,
                            StringComparison.Ordinal))
                    {
                        throw new InvalidDataException(
                            $"quest '{quest.questId}' has an invalid personal-reward source");
                    }
                    if (!rewardSource.conversionMarker.StartsWith(
                            PersonalRewardSourceMarkerPrefix,
                            StringComparison.Ordinal))
                    {
                        throw new InvalidDataException(
                            $"quest '{quest.questId}' has an invalid personal-reward " +
                            $"source marker '{rewardSource.conversionMarker}'");
                    }
                    if (!fileRewardSourceTags.Add(rewardSource.containerCustomTag))
                    {
                        throw new InvalidDataException(
                            $"duplicate personal-reward source container tag " +
                            $"'{rewardSource.containerCustomTag}'");
                    }
                    foreach (KeyValuePair<string, StoryCatalogQuest> existing in Quests)
                    {
                        if (string.Equals(
                                existing.Value.policy?.personalRewardSource?.containerCustomTag,
                                rewardSource.containerCustomTag,
                                StringComparison.Ordinal))
                        {
                            throw new InvalidDataException(
                                $"personal-reward source container tag " +
                                $"'{rewardSource.containerCustomTag}' is already owned by " +
                                $"quest '{existing.Key}'");
                        }
                    }
                    if ((quest.policy.observerSuccess?.personalItemGrants?.Length ?? 0) == 0)
                    {
                        throw new InvalidDataException(
                            $"quest '{quest.questId}' personal-reward source has no " +
                            "personal item grants");
                    }
                }
            }

            StoryCatalogTaskMilestone[] taskMilestones = file.taskMilestones ??
                new StoryCatalogTaskMilestone[0];
            for (int i = 0; i < taskMilestones.Length; i++)
            {
                StoryCatalogTaskMilestone milestone = taskMilestones[i];
                if (milestone == null ||
                    string.IsNullOrWhiteSpace(milestone.milestoneId) ||
                    string.IsNullOrWhiteSpace(milestone.npcId) ||
                    string.IsNullOrWhiteSpace(milestone.taskId))
                {
                    throw new InvalidDataException(
                        $"task milestone #{i} requires milestoneId, npcId, and taskId");
                }
                if (milestone.activeStates == null || milestone.activeStates.Length == 0)
                {
                    throw new InvalidDataException(
                        $"task milestone '{milestone.milestoneId}' has no active states");
                }

                var states = new HashSet<string>(StringComparer.Ordinal);
                for (int j = 0; j < milestone.activeStates.Length; j++)
                {
                    string state = milestone.activeStates[j];
                    if (!IsOneOf(state, "Visible", "Complete") || !states.Add(state))
                    {
                        throw new InvalidDataException(
                            $"task milestone '{milestone.milestoneId}' has invalid or " +
                            $"duplicate active state '{state}'");
                    }
                }

                StoryCatalogPlayerParamRepair[] minimums =
                    milestone.playerParamMinimums ??
                    new StoryCatalogPlayerParamRepair[0];
                ValidatePlayerParamRepairs(
                    milestone.playerParamRepairs,
                    $"task milestone '{milestone.milestoneId}' parameter repair");
                ValidatePlayerParamRepairs(
                    minimums,
                    $"task milestone '{milestone.milestoneId}' parameter minimum");
                ValidateTaskStateRepairs(
                    milestone.taskStateMinimums,
                    $"task milestone '{milestone.milestoneId}' task minimum");
                ValidateNonEmptyValues(
                    milestone.unlockedTechs,
                    $"task milestone '{milestone.milestoneId}' unlocked tech");
                ValidateNonEmptyValues(
                    milestone.unlockedPhrases,
                    $"task milestone '{milestone.milestoneId}' unlocked phrase");
                ValidateNonEmptyValues(
                    milestone.blacklistedPhrases,
                    $"task milestone '{milestone.milestoneId}' blacklisted phrase");
                ValidateNonEmptyValues(
                    milestone.unlockedCrafts,
                    $"task milestone '{milestone.milestoneId}' unlocked craft");
                ValidateNonEmptyValues(
                    milestone.completedOneTimeCrafts,
                    $"task milestone '{milestone.milestoneId}' completed craft");

                StoryCatalogWgoParamRepair[] wgoMinimums =
                    milestone.wgoParamMinimums ??
                    new StoryCatalogWgoParamRepair[0];
                for (int j = 0; j < wgoMinimums.Length; j++)
                {
                    if (wgoMinimums[j] == null ||
                        string.IsNullOrWhiteSpace(wgoMinimums[j].objectId) ||
                        string.IsNullOrWhiteSpace(wgoMinimums[j].key) ||
                        float.IsNaN(wgoMinimums[j].value) ||
                        float.IsInfinity(wgoMinimums[j].value))
                    {
                        throw new InvalidDataException(
                            $"task milestone '{milestone.milestoneId}' world-object " +
                            $"minimum #{j} is invalid");
                    }
                }

                StoryCatalogPersonalItemGrant[] grants =
                    milestone.personalItemGrants ??
                    new StoryCatalogPersonalItemGrant[0];
                ValidatePersonalItemGrants(
                    grants,
                    $"task milestone '{milestone.milestoneId}'",
                    fileReceiptIds);
            }

            StoryCatalogCutscene[] cutscenes = file.cutscenes ??
                new StoryCatalogCutscene[0];
            for (int i = 0; i < cutscenes.Length; i++)
            {
                StoryCatalogCutscene cutscene = cutscenes[i];
                if (cutscene == null || string.IsNullOrWhiteSpace(cutscene.cutsceneId))
                    throw new InvalidDataException($"cutscene #{i} has no cutsceneId");
                if (cutscene.triggerScripts == null || cutscene.triggerScripts.Length == 0)
                {
                    throw new InvalidDataException(
                        $"cutscene '{cutscene.cutsceneId}' has no trigger scripts");
                }
                if (!IsOneOf(
                        cutscene.remoteExecutionPolicy,
                        LocalMirrorRequired,
                        LiveObservation))
                {
                    throw new InvalidDataException(
                        $"cutscene '{cutscene.cutsceneId}' uses unsupported remote " +
                        $"execution policy '{cutscene.remoteExecutionPolicy}'");
                }
                if (!IsOneOf(
                        cutscene.executionOwnerPolicy,
                        "any_player",
                        LobbyHost))
                {
                    throw new InvalidDataException(
                        $"cutscene '{cutscene.cutsceneId}' uses unsupported " +
                        $"execution owner policy '{cutscene.executionOwnerPolicy}'");
                }
                if (!IsOneOf(
                        cutscene.dialogueOwnerPolicy,
                        LobbyHost,
                        InitiatingPlayer))
                {
                    throw new InvalidDataException(
                        $"cutscene '{cutscene.cutsceneId}' uses unsupported dialogue " +
                        $"owner policy '{cutscene.dialogueOwnerPolicy}'");
                }
                if (!string.IsNullOrEmpty(cutscene.participantFacing) &&
                    !IsOneOf(
                        cutscene.participantFacing,
                        "Right",
                        "Up",
                        "Left",
                        "Down"))
                {
                    throw new InvalidDataException(
                        $"cutscene '{cutscene.cutsceneId}' uses unsupported " +
                        $"participant facing '{cutscene.participantFacing}'");
                }

                StoryCatalogPersonalItemGrant[] grants =
                    cutscene.personalItemGrants ??
                    new StoryCatalogPersonalItemGrant[0];
                ValidatePersonalItemGrants(
                    grants,
                    $"cutscene '{cutscene.cutsceneId}'",
                    fileReceiptIds);
                ValidateStateChecks(
                    cutscene.completionChecks,
                    $"cutscene '{cutscene.cutsceneId}' completion check");
                if (grants.Length > 0 &&
                    ((cutscene.personalGrantPrerequisiteQuestIds?.Length ?? 0) == 0 ||
                     (cutscene.completionChecks?.Length ?? 0) == 0))
                {
                    throw new InvalidDataException(
                        $"cutscene '{cutscene.cutsceneId}' personal grants require " +
                        "prerequisite quests and durable completion checks");
                }
                ValidateNonEmptyValues(
                    cutscene.personalGrantPrerequisiteQuestIds,
                    $"cutscene '{cutscene.cutsceneId}' personal-grant prerequisite quest");
            }

            StoryCatalogStep[] storySteps = file.storySteps ??
                new StoryCatalogStep[0];
            for (int i = 0; i < storySteps.Length; i++)
            {
                StoryCatalogStep step = storySteps[i];
                if (step == null || string.IsNullOrWhiteSpace(step.stepId) ||
                    string.IsNullOrWhiteSpace(step.title))
                {
                    throw new InvalidDataException(
                        $"story step #{i} requires stepId and title");
                }
                bool hasLinkedQuests = step.linkedQuestIds != null &&
                    step.linkedQuestIds.Length > 0;
                bool hasLinkedTaskMilestones = step.linkedTaskMilestoneIds != null &&
                    step.linkedTaskMilestoneIds.Length > 0;
                if (!hasLinkedQuests && !hasLinkedTaskMilestones)
                {
                    throw new InvalidDataException(
                        $"story step '{step.stepId}' has no linked quests or task milestones");
                }

                StoryCatalogExpectedEffect[] effects = step.expectedEffects ??
                    new StoryCatalogExpectedEffect[0];
                for (int j = 0; j < effects.Length; j++)
                {
                    StoryCatalogExpectedEffect effect = effects[j];
                    if (effect == null ||
                        string.IsNullOrWhiteSpace(effect.kind) ||
                        string.IsNullOrWhiteSpace(effect.id) ||
                        string.IsNullOrWhiteSpace(effect.operation) ||
                        string.IsNullOrWhiteSpace(effect.ownership))
                    {
                        throw new InvalidDataException(
                            $"story step '{step.stepId}' effect #{j} is incomplete");
                    }
                }
            }
        }

        private static void RegisterFile(StoryCatalogFile file)
        {
            CatalogIds.Add(file.catalogId);
            for (int i = 0; i < file.quests.Length; i++)
            {
                StoryCatalogQuest quest = file.quests[i];
                if (Quests.ContainsKey(quest.questId))
                    throw new InvalidDataException($"duplicate questId '{quest.questId}'");

                quest.catalogId = file.catalogId;
                Quests.Add(quest.questId, quest);
                StoryCatalogPersonalItemGrant[] grants =
                    quest.policy?.observerSuccess?.personalItemGrants ??
                    new StoryCatalogPersonalItemGrant[0];
                for (int j = 0; j < grants.Length; j++)
                {
                    if (!PersonalGrantReceiptIds.Add(grants[j].receiptId))
                    {
                        throw new InvalidDataException(
                            $"duplicate personal-item receipt " +
                            $"'{grants[j].receiptId}'");
                    }
                }
                RegisterQuestScript(quest.vanilla.startScript, quest.questId);
                RegisterQuestScript(quest.vanilla.successScript, quest.questId);
                RegisterQuestScript(quest.vanilla.failScript, quest.questId);
            }

            StoryCatalogTaskMilestone[] taskMilestones = file.taskMilestones ??
                new StoryCatalogTaskMilestone[0];
            for (int i = 0; i < taskMilestones.Length; i++)
            {
                StoryCatalogTaskMilestone milestone = taskMilestones[i];
                if (TaskMilestones.ContainsKey(milestone.milestoneId))
                {
                    throw new InvalidDataException(
                        $"duplicate milestoneId '{milestone.milestoneId}'");
                }

                milestone.catalogId = file.catalogId;
                TaskMilestones.Add(milestone.milestoneId, milestone);
                StoryCatalogPersonalItemGrant[] grants =
                    milestone.personalItemGrants ??
                    new StoryCatalogPersonalItemGrant[0];
                for (int j = 0; j < grants.Length; j++)
                {
                    if (!PersonalGrantReceiptIds.Add(grants[j].receiptId))
                    {
                        throw new InvalidDataException(
                            $"duplicate personal-item receipt " +
                            $"'{grants[j].receiptId}'");
                    }
                }
            }

            StoryCatalogCutscene[] cutscenes = file.cutscenes ??
                new StoryCatalogCutscene[0];
            for (int i = 0; i < cutscenes.Length; i++)
            {
                StoryCatalogCutscene cutscene = cutscenes[i];
                if (cutscene == null || string.IsNullOrWhiteSpace(cutscene.cutsceneId))
                    throw new InvalidDataException($"cutscene #{i} has no cutsceneId");
                if (Cutscenes.ContainsKey(cutscene.cutsceneId))
                {
                    throw new InvalidDataException(
                        $"duplicate cutsceneId '{cutscene.cutsceneId}'");
                }

                cutscene.catalogId = file.catalogId;
                Cutscenes.Add(cutscene.cutsceneId, cutscene);
                StoryCatalogPersonalItemGrant[] grants =
                    cutscene.personalItemGrants ??
                    new StoryCatalogPersonalItemGrant[0];
                for (int j = 0; j < grants.Length; j++)
                {
                    if (!PersonalGrantReceiptIds.Add(grants[j].receiptId))
                    {
                        throw new InvalidDataException(
                            $"duplicate personal-item receipt " +
                            $"'{grants[j].receiptId}'");
                    }
                }
                string[] triggerScripts = cutscene.triggerScripts ?? new string[0];
                for (int j = 0; j < triggerScripts.Length; j++)
                    RegisterCutsceneScript(triggerScripts[j], cutscene.cutsceneId);
            }

            StoryCatalogStep[] storySteps = file.storySteps ??
                new StoryCatalogStep[0];
            for (int i = 0; i < storySteps.Length; i++)
            {
                StoryCatalogStep step = storySteps[i];
                if (StorySteps.ContainsKey(step.stepId))
                    throw new InvalidDataException($"duplicate stepId '{step.stepId}'");

                step.catalogId = file.catalogId;
                StorySteps.Add(step.stepId, step);
                string[] triggerScripts = step.triggerScripts ?? new string[0];
                for (int j = 0; j < triggerScripts.Length; j++)
                    RegisterStepScript(triggerScripts[j], step.stepId);
            }
        }

        private static void RegisterQuestScript(string scriptName, string questId)
        {
            if (string.IsNullOrEmpty(scriptName))
                return;

            ScriptBinding binding = GetScriptBinding(scriptName);
            AddUnique(binding.QuestIds, questId);
        }

        private static void RegisterCutsceneScript(
            string scriptName,
            string cutsceneId)
        {
            if (string.IsNullOrEmpty(scriptName))
                return;

            ScriptBinding binding = GetScriptBinding(scriptName);
            AddUnique(binding.CutsceneIds, cutsceneId);
        }

        private static void RegisterStepScript(string scriptName, string stepId)
        {
            if (string.IsNullOrEmpty(scriptName))
                return;

            ScriptBinding binding = GetScriptBinding(scriptName);
            AddUnique(binding.StepIds, stepId);
        }

        private static ScriptBinding GetScriptBinding(string scriptName)
        {
            if (!Scripts.TryGetValue(scriptName, out ScriptBinding binding))
            {
                binding = new ScriptBinding();
                Scripts.Add(scriptName, binding);
            }
            return binding;
        }

        private static void ValidateLinks()
        {
            foreach (KeyValuePair<string, StoryCatalogQuest> pair in Quests)
            {
                ValidateRepairGuardLinks(
                    pair.Value.policy?.observerStart?.playerParamRepairs,
                    $"quest '{pair.Key}' observer-start repair",
                    pair.Key);
                ValidateRepairGuardLinks(
                    pair.Value.policy?.observerStart?.playerParamMinimums,
                    $"quest '{pair.Key}' observer-start minimum",
                    pair.Key);
                ValidateRepairGuardLinks(
                    pair.Value.policy?.observerSuccess?.playerParamRepairs,
                    $"quest '{pair.Key}' observer-success repair",
                    pair.Key);
                ValidateRepairGuardLinks(
                    pair.Value.policy?.observerSuccess?.playerParamMinimums,
                    $"quest '{pair.Key}' observer-success minimum",
                    pair.Key);
            }

            foreach (StoryCatalogTaskMilestone milestone in TaskMilestones.Values)
            {
                ValidateRepairGuardLinks(
                    milestone.playerParamRepairs,
                    $"task milestone '{milestone.milestoneId}' repair",
                    null);
                ValidateRepairGuardLinks(
                    milestone.playerParamMinimums,
                    $"task milestone '{milestone.milestoneId}' minimum",
                    null);
            }

            foreach (StoryCatalogCutscene cutscene in Cutscenes.Values)
            {
                string[] linkedQuestIds = cutscene.linkedQuestIds ?? new string[0];
                for (int i = 0; i < linkedQuestIds.Length; i++)
                {
                    if (!Quests.ContainsKey(linkedQuestIds[i]))
                    {
                        CoopMod.Logger.LogError(
                            $"[StoryCatalog] Cutscene '{cutscene.cutsceneId}' links " +
                            $"unknown quest '{linkedQuestIds[i]}'");
                    }
                }
            }

            foreach (StoryCatalogStep step in StorySteps.Values)
            {
                string[] linkedQuestIds = step.linkedQuestIds ?? new string[0];
                for (int i = 0; i < linkedQuestIds.Length; i++)
                {
                    if (!Quests.ContainsKey(linkedQuestIds[i]))
                    {
                        CoopMod.Logger.LogError(
                            $"[StoryCatalog] Story step '{step.stepId}' links " +
                            $"unknown quest '{linkedQuestIds[i]}'");
                    }
                }

                string[] linkedTaskMilestoneIds =
                    step.linkedTaskMilestoneIds ?? new string[0];
                for (int i = 0; i < linkedTaskMilestoneIds.Length; i++)
                {
                    if (!TaskMilestones.ContainsKey(linkedTaskMilestoneIds[i]))
                    {
                        CoopMod.Logger.LogError(
                            $"[StoryCatalog] Story step '{step.stepId}' links unknown " +
                            $"task milestone '{linkedTaskMilestoneIds[i]}'");
                    }
                }
            }
        }

        private static void ValidateRepairGuardLinks(
            StoryCatalogPlayerParamRepair[] repairs,
            string owner,
            string ownerQuestId)
        {
            if (repairs == null)
                return;

            for (int i = 0; i < repairs.Length; i++)
            {
                var seen = new HashSet<string>(StringComparer.Ordinal);
                ValidateRepairGuardArray(
                    repairs[i]?.unlessQuestSucceeded,
                    owner,
                    ownerQuestId,
                    i,
                    seen);
                ValidateRepairGuardArray(
                    repairs[i]?.unlessQuestReached,
                    owner,
                    ownerQuestId,
                    i,
                    seen);
            }
        }

        private static void ValidateRepairGuardArray(
            string[] guards,
            string owner,
            string ownerQuestId,
            int repairIndex,
            HashSet<string> seen)
        {
            guards = guards ?? new string[0];
            for (int j = 0; j < guards.Length; j++)
            {
                string guard = guards[j];
                if (!seen.Add(guard))
                {
                    throw new InvalidDataException(
                        $"{owner} #{repairIndex} has duplicate downstream guard '{guard}'");
                }
                if (string.Equals(guard, ownerQuestId, StringComparison.Ordinal))
                {
                    throw new InvalidDataException(
                        $"{owner} #{repairIndex} cannot guard itself with '{guard}'");
                }
                if (!Quests.ContainsKey(guard))
                {
                    throw new InvalidDataException(
                        $"{owner} #{repairIndex} links unknown downstream quest '{guard}'");
                }
            }
        }

        private static StoryCatalogObserverSuccess GetObserverSuccess(string questId)
        {
            return GetObserverEffects(questId, success: true);
        }

        private static StoryCatalogObserverSuccess GetObserverEffects(
            string questId,
            bool success)
        {
            EnsureInitialized();
            if (string.IsNullOrEmpty(questId) ||
                !Quests.TryGetValue(questId, out StoryCatalogQuest quest))
            {
                return null;
            }
            return success
                ? quest.policy?.observerSuccess
                : quest.policy?.observerStart;
        }

        private static bool HasDurableEffects(StoryCatalogObserverSuccess effects)
        {
            return (effects?.globalScripts?.Length ?? 0) > 0 ||
                   (effects?.playerParamRepairs?.Length ?? 0) > 0 ||
                   (effects?.playerParamMinimums?.Length ?? 0) > 0 ||
                   (effects?.taskStateMinimums?.Length ?? 0) > 0 ||
                   (effects?.unlockedTechs?.Length ?? 0) > 0 ||
                   (effects?.unlockedPhrases?.Length ?? 0) > 0 ||
                   (effects?.blacklistedPhrases?.Length ?? 0) > 0 ||
                   (effects?.unlockedCrafts?.Length ?? 0) > 0 ||
                   (effects?.completedOneTimeCrafts?.Length ?? 0) > 0 ||
                   (effects?.personalItemGrants?.Length ?? 0) > 0;
        }

        private static StoryCatalogCutscene GetCutscenePolicy(string scriptName)
        {
            EnsureInitialized();
            if (string.IsNullOrEmpty(scriptName) ||
                !Scripts.TryGetValue(scriptName, out ScriptBinding binding) ||
                binding.CutsceneIds.Count == 0 ||
                !Cutscenes.TryGetValue(
                    binding.CutsceneIds[0],
                    out StoryCatalogCutscene cutscene))
            {
                return null;
            }

            string eventKey = $"cutscene-policy:{scriptName}";
            if (LoggedEvents.Add(eventKey))
            {
                CoopMod.Logger.LogInfo(
                    $"[StoryCatalog] FlowScript '{scriptName}' uses cutscene " +
                    $"'{cutscene.cutsceneId}': remote={cutscene.remoteExecutionPolicy}, " +
                    $"dialogue_owner={cutscene.dialogueOwnerPolicy}");
            }
            return cutscene;
        }

        private static bool ValidateQuestDefinition(
            StoryCatalogQuest record,
            QuestDefinition actual)
        {
            if (!ValidatedQuests.Add(record.questId))
                return !InvalidQuestDefinitions.Contains(record.questId);

            StoryCatalogQuestDefinition expected = record.vanilla;
            var mismatches = new List<string>();
            Compare(mismatches, "startTrigger", expected.startTrigger,
                Raw(actual.start_trigger));
            Compare(mismatches, "successTrigger", expected.successTrigger,
                Raw(actual.success_trigger));
            Compare(mismatches, "failTrigger", expected.failTrigger,
                Raw(actual.fail_trigger));
            Compare(mismatches, "startScript", expected.startScript,
                actual.start_script ?? string.Empty);
            Compare(mismatches, "successScript", expected.successScript,
                actual.success_script ?? string.Empty);
            Compare(mismatches, "failScript", expected.failScript,
                actual.fail_script ?? string.Empty);
            Compare(mismatches, "arrowWgoCustomTag", expected.arrowWgoCustomTag,
                actual.arrow_wgo_custom_tag ?? string.Empty);
            Compare(mismatches, "arrowWgoObjectId", expected.arrowWgoObjectId,
                actual.arrow_wgo_obj_id ?? string.Empty);

            if (expected.questVisible != actual.quest_visible)
            {
                mismatches.Add(
                    $"questVisible expected={expected.questVisible} actual={actual.quest_visible}");
            }
            if (expected.oneTimeQuest != actual.one_time_quest)
            {
                mismatches.Add(
                    $"oneTimeQuest expected={expected.oneTimeQuest} actual={actual.one_time_quest}");
            }

            CompareStartKeys(mismatches, expected.startKeys, actual.start_key);
            CompareRewards(
                mismatches,
                "successRewards",
                expected.successRewards,
                actual.rew_on_success_params);
            CompareRewards(
                mismatches,
                "failureRewards",
                expected.failureRewards,
                actual.rew_on_fail_params);

            if (mismatches.Count == 0)
            {
                CoopMod.Logger.LogInfo(
                    $"[StoryCatalog] Validated QuestDefinition '{record.questId}' " +
                    $"against catalog '{record.catalogId}'");
                return true;
            }

            InvalidQuestDefinitions.Add(record.questId);
            CoopMod.Logger.LogWarning(
                $"[StoryCatalog] QuestDefinition '{record.questId}' differs from " +
                $"catalog '{record.catalogId}': {string.Join("; ", mismatches.ToArray())}");
            return false;
        }

        private static void ValidateCompletionChecks(StoryCatalogQuest record)
        {
            StoryCatalogStateCheck[] checks = record.completionChecks ??
                new StoryCatalogStateCheck[0];
            for (int i = 0; i < checks.Length; i++)
            {
                StoryCatalogStateCheck check = checks[i];
                if (check == null ||
                    !string.Equals(check.source, "player_param", StringComparison.Ordinal))
                {
                    continue;
                }

                WorldGameObject player = MainGame.me?.player;
                if (player?.data == null)
                    continue;

                float actual = player.data.GetParam(check.key, 0f);
                bool passed = CompareValue(actual, check.comparison, check.value);
                if (!passed)
                {
                    CoopMod.Logger.LogWarning(
                        $"[StoryCatalog] Quest '{record.questId}' completed but " +
                        $"player_param '{check.key}' was {actual}; expected " +
                        $"{check.comparison} {check.value}");
                }
            }
        }

        private static bool CompareValue(float actual, string comparison, float expected)
        {
            switch (comparison)
            {
                case ">=": return actual >= expected;
                case ">": return actual > expected;
                case "<=": return actual <= expected;
                case "<": return actual < expected;
                case "==": return Mathf.Abs(actual - expected) < 0.0001f;
                default: return false;
            }
        }

        private static bool IsOneOf(string value, string first, string second)
        {
            return string.Equals(value, first, StringComparison.Ordinal) ||
                   string.Equals(value, second, StringComparison.Ordinal);
        }

        private static bool IsOneOf(
            string value,
            string first,
            string second,
            string third,
            string fourth)
        {
            return string.Equals(value, first, StringComparison.Ordinal) ||
                   string.Equals(value, second, StringComparison.Ordinal) ||
                   string.Equals(value, third, StringComparison.Ordinal) ||
                   string.Equals(value, fourth, StringComparison.Ordinal);
        }

        private static bool IsOneOf(
            string value,
            string first,
            string second,
            string third,
            string fourth,
            string fifth)
        {
            return IsOneOf(value, first, second, third, fourth) ||
                   string.Equals(value, fifth, StringComparison.Ordinal);
        }

        private static void Compare(
            List<string> mismatches,
            string field,
            string expected,
            string actual)
        {
            if (expected == null)
                return;
            if (!string.Equals(expected, actual ?? string.Empty, StringComparison.Ordinal))
                mismatches.Add($"{field} expected='{expected}' actual='{actual}'");
        }

        private static void CompareStartKeys(
            List<string> mismatches,
            string[] expected,
            List<string> actual)
        {
            if (expected == null)
                return;

            int actualCount = actual?.Count ?? 0;
            if (expected.Length != actualCount)
            {
                mismatches.Add(
                    $"startKeys expected=[{Join(expected)}] actual=[{Join(actual)}]");
                return;
            }

            for (int i = 0; i < expected.Length; i++)
            {
                if (!string.Equals(expected[i], actual[i], StringComparison.Ordinal))
                {
                    mismatches.Add(
                        $"startKeys expected=[{Join(expected)}] actual=[{Join(actual)}]");
                    return;
                }
            }
        }

        private static void CompareRewards(
            List<string> mismatches,
            string field,
            StoryCatalogReward[] expected,
            GameRes actual)
        {
            if (expected == null)
                return;

            int actualCount = CountRewardValues(actual);
            if (expected.Length != actualCount)
            {
                mismatches.Add(
                    $"{field} expectedCount={expected.Length} actualCount={actualCount}");
                return;
            }

            for (int i = 0; i < expected.Length; i++)
            {
                StoryCatalogReward reward = expected[i];
                if (reward == null || string.IsNullOrEmpty(reward.type))
                {
                    mismatches.Add($"{field}[{i}] is invalid");
                    continue;
                }

                float actualAmount = actual.Get(reward.type, float.NaN);
                if (float.IsNaN(actualAmount) ||
                    Mathf.Abs(actualAmount - reward.amount) >= 0.0001f)
                {
                    mismatches.Add(
                        $"{field}[{reward.type}] expected={reward.amount} actual={actualAmount}");
                }
            }
        }

        private static int CountRewardValues(GameRes rewards)
        {
            if (rewards == null)
                return 0;

            int count = rewards.Types?.Count ?? 0;
            string[] builtInTypes = { "hp", "progress", "money", "durability" };
            for (int i = 0; i < builtInTypes.Length; i++)
            {
                if (Mathf.Abs(rewards.Get(builtInTypes[i], 0f)) >= 0.0001f)
                    count++;
            }
            return count;
        }

        private static string Raw(SmartExpression expression)
        {
            return expression?.GetRawExpressionString() ?? string.Empty;
        }

        private static void AddUnique(List<string> values, string value)
        {
            if (!values.Contains(value))
                values.Add(value);
        }

        private static void ValidateObserverEffects(
            StoryCatalogObserverSuccess effects,
            string owner,
            HashSet<string> receiptIds)
        {
            if (effects == null)
                return;

            ValidateNonEmptyValues(effects.globalScripts, owner + " script");
            ValidatePlayerParamRepairs(
                effects.playerParamRepairs,
                owner + " player-param repair");
            ValidatePlayerParamRepairs(
                effects.playerParamMinimums,
                owner + " player-param minimum");
            ValidateTaskStateRepairs(
                effects.taskStateMinimums,
                owner + " task minimum");
            ValidateNonEmptyValues(effects.unlockedTechs, owner + " unlocked tech");
            ValidateNonEmptyValues(
                effects.unlockedPhrases,
                owner + " unlocked phrase");
            ValidateNonEmptyValues(
                effects.blacklistedPhrases,
                owner + " blacklisted phrase");
            ValidateNonEmptyValues(effects.unlockedCrafts, owner + " unlocked craft");
            ValidateNonEmptyValues(
                effects.completedOneTimeCrafts,
                owner + " completed craft");
            ValidatePersonalItemGrants(
                effects.personalItemGrants ?? new StoryCatalogPersonalItemGrant[0],
                owner,
                receiptIds);
        }

        private static void ValidatePlayerParamRepairs(
            StoryCatalogPlayerParamRepair[] repairs,
            string owner)
        {
            if (repairs == null)
                return;

            for (int i = 0; i < repairs.Length; i++)
            {
                StoryCatalogPlayerParamRepair repair = repairs[i];
                if (repair == null ||
                    string.IsNullOrWhiteSpace(repair.key) ||
                    float.IsNaN(repair.value) ||
                    float.IsInfinity(repair.value))
                {
                    throw new InvalidDataException($"{owner} #{i} is invalid");
                }
                ValidateNonEmptyValues(
                    repair.unlessQuestSucceeded,
                    $"{owner} #{i} downstream quest guard");
                ValidateNonEmptyValues(
                    repair.unlessQuestReached,
                    $"{owner} #{i} downstream reached-quest guard");
            }
        }

        private static void ValidateTaskStateRepairs(
            StoryCatalogTaskStateRepair[] repairs,
            string owner)
        {
            if (repairs == null)
                return;

            for (int i = 0; i < repairs.Length; i++)
            {
                StoryCatalogTaskStateRepair repair = repairs[i];
                if (repair == null ||
                    string.IsNullOrWhiteSpace(repair.npcId) ||
                    string.IsNullOrWhiteSpace(repair.taskId) ||
                    !IsOneOf(repair.state, "Visible", "Complete"))
                {
                    throw new InvalidDataException($"{owner} #{i} is invalid");
                }
            }
        }

        private static void ValidatePersonalItemGrants(
            StoryCatalogPersonalItemGrant[] grants,
            string owner,
            HashSet<string> receiptIds)
        {
            for (int i = 0; i < grants.Length; i++)
            {
                StoryCatalogPersonalItemGrant grant = grants[i];
                if (grant == null ||
                    string.IsNullOrWhiteSpace(grant.itemId) ||
                    string.IsNullOrWhiteSpace(grant.receiptId))
                {
                    throw new InvalidDataException(
                        $"{owner} personal-item grant #{i} is incomplete");
                }
                if (grant.count < 1 || grant.count > 999)
                {
                    throw new InvalidDataException(
                        $"{owner} personal-item grant #{i} has invalid count " +
                        $"{grant.count}");
                }
                if (!grant.receiptId.StartsWith(
                        PersonalGrantReceiptPrefix,
                        StringComparison.Ordinal))
                {
                    throw new InvalidDataException(
                        $"{owner} personal-item grant #{i} has invalid receipt " +
                        $"'{grant.receiptId}'");
                }
                if (!receiptIds.Add(grant.receiptId))
                {
                    throw new InvalidDataException(
                        $"duplicate personal-item receipt '{grant.receiptId}'");
                }
            }
        }

        private static void ValidateStateChecks(
            StoryCatalogStateCheck[] checks,
            string owner)
        {
            if (checks == null)
                return;

            for (int i = 0; i < checks.Length; i++)
            {
                StoryCatalogStateCheck check = checks[i];
                if (check == null ||
                    !string.Equals(
                        check.source,
                        "player_param",
                        StringComparison.Ordinal) ||
                    string.IsNullOrWhiteSpace(check.key) ||
                    !IsOneOf(
                        check.comparison,
                        ">=",
                        ">",
                        "<=",
                        "<",
                        "==") ||
                    float.IsNaN(check.value) ||
                    float.IsInfinity(check.value))
                {
                    throw new InvalidDataException($"{owner} #{i} is invalid");
                }
            }
        }

        private static void ValidateNonEmptyValues(string[] values, string field)
        {
            if (values == null)
                return;

            for (int i = 0; i < values.Length; i++)
            {
                if (string.IsNullOrWhiteSpace(values[i]))
                {
                    throw new InvalidDataException(
                        $"{field} #{i} must not be empty");
                }
            }
        }

        private static string Join(IList<string> values)
        {
            if (values == null || values.Count == 0)
                return string.Empty;

            var copy = new string[values.Count];
            for (int i = 0; i < values.Count; i++)
                copy[i] = values[i];
            return string.Join(",", copy);
        }

        private static void EnsureInitialized()
        {
            if (!initialized)
                Initialize();
        }
    }
}

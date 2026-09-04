using System;
using System.Collections.Generic;
using System.Reflection;
using GraveyardKeeperCoop.Network;
using HarmonyLib;
using Steamworks;
using UnityEngine;

namespace GraveyardKeeperCoop.Multiplayer
{
    /// <summary>
    /// Host-coordinated player trading. Items and money leave the local player's
    /// inventory as soon as they enter the offer and remain in a private escrow
    /// until the trade commits or is cancelled.
    /// </summary>
    public sealed class PlayerTradeManager : MonoBehaviour
    {
        private sealed class HostTrade
        {
            public ulong SessionId;
            public CSteamID Inviter;
            public CSteamID Invitee;
            public int InviterRevision;
            public int InviteeRevision;
            public List<string> InviterOffer = new List<string>();
            public List<string> InviteeOffer = new List<string>();
            public int InviterMoneyOfferBronze;
            public int InviteeMoneyOfferBronze;
            public string InviterInventoryJson = string.Empty;
            public string InviteeInventoryJson = string.Empty;
            public byte InviterProtocolVersion;
            public byte InviteeProtocolVersion;
            public bool MoneyTradingEnabled;
            public bool InviterReady;
            public bool InviteeReady;
            public bool Begun;
            public bool Committed;
            public bool InviterCommitAck;
            public bool InviteeCommitAck;
            public float NextCommitSendAt;
            public float LastActivityAt;
        }

        private const float InteractionDistance = 112f;
        private const float ActiveTradeDistance = 224f;
        private const float InviteTimeoutSeconds = 30f;
        private const float ActiveTimeoutSeconds = 180f;
        private const int OfferSlots = 6;
        private const int MaxItemJsonLength = 128 * 1024;
        private const int MaxOfferJsonLength = 384 * 1024;
        private const int MaxInventoryJsonLength = 256 * 1024;
        private const int MaxMoneyOfferBronze = 999999999;
        private const byte TradeProtocolVersion = 2;

        private static readonly FieldInfo PlayerField = AccessTools.Field(typeof(VendorGUI), "_player");
        private static readonly FieldInfo VendorField = AccessTools.Field(typeof(VendorGUI), "_vendor");
        private static readonly FieldInfo VendorRealField = AccessTools.Field(typeof(VendorGUI), "_vendor_real");
        private static readonly FieldInfo PlayerOfferField = AccessTools.Field(typeof(VendorGUI), "_player_offer");
        private static readonly FieldInfo VendorOfferField = AccessTools.Field(typeof(VendorGUI), "_vendor_offer");
        private static readonly FieldInfo SelectedPanelField = AccessTools.Field(typeof(VendorGUI), "_selected_panel");
        private static readonly FieldInfo SelectedWidgetField = AccessTools.Field(typeof(VendorGUI), "_selected_widget");
        private static readonly FieldInfo SelectedItemGuiField = AccessTools.Field(typeof(VendorGUI), "_selected_item_gui");
        private static readonly FieldInfo SelectedItemField = AccessTools.Field(typeof(VendorGUI), "_selected_item");
        private static readonly FieldInfo VendorObjectField = AccessTools.Field(typeof(VendorGUI), "_vendor_obj");
        private static readonly FieldInfo EnableTimeAfterCloseField = AccessTools.Field(typeof(VendorGUI), "_enable_time_after_close");
        private static readonly FieldInfo ItemCountItemGuiField = AccessTools.Field(typeof(ItemCountGUI), "_item_gui");
        private static readonly FieldInfo ItemCountSliderField = AccessTools.Field(typeof(ItemCountGUI), "_slider");
        private static readonly FieldInfo ProgressBarBackgroundField = AccessTools.Field(typeof(UIProgressBar), "mBG");
        private static readonly FieldInfo NavigationFocusField = AccessTools.Field(typeof(GamepadNavigationItem), "_on_focus");
        private static readonly FieldInfo NavigationUnfocusField = AccessTools.Field(typeof(GamepadNavigationItem), "_on_unfocus");
        private static readonly FieldInfo NavigationPressedField = AccessTools.Field(typeof(GamepadNavigationItem), "_on_pressed");

        private static PlayerTradeManager instance;
        public static PlayerTradeManager Instance => instance;

        private readonly Dictionary<ulong, HostTrade> hostTrades = new Dictionary<ulong, HostTrade>();
        private readonly List<ulong> expiredHostTrades = new List<ulong>();
        private readonly HashSet<ulong> completedSessions = new HashSet<ulong>();

        private ulong localSessionId;
        private CSteamID localPartner = CSteamID.Nil;
        private bool incomingInviteVisible;
        private bool tradeBegun;
        private bool localReady;
        private bool remoteReady;
        private int localRevision;
        private int remoteRevision;
        private int localMoneyOfferBronze;
        private int remoteMoneyOfferBronze;
        private bool moneyTradingEnabled;
        private float localSessionLastActivityAt;
        private MultiInventory localInventory;
        private Item remoteInventoryRoot;
        private Item localOfferRoot;
        private Item remoteOfferRoot;
        private MultiInventory remoteInventory;
        private MultiInventory localOfferInventory;
        private MultiInventory remoteOfferInventory;
        private VendorGUI tradeGui;
        private bool closingTradeGui;
        private bool committingTrade;
        private GameObject moneyTradeHitbox;
        private UILabel moneyTradeLabel;
        private Color moneyLabelNormalColor;
        private BoxCollider2D moneyTradeCollider;
        private bool ownsMoneyTradeCollider;
        private bool moneyTradeColliderWasEnabled;
        private bool moneyTradeColliderWasTrigger;
        private Vector2 moneyTradeColliderOriginalSize;
        private Vector2 moneyTradeColliderOriginalOffset;
        private UIEventListener moneyTradeListener;
        private bool ownsMoneyTradeListener;
        private GamepadNavigationItem moneyTradeNavigation;
        private bool ownsMoneyTradeNavigation;
        private PanelAutoScroll moneyTradeAutoScroll;
        private bool ownsMoneyTradeAutoScroll;
        private object moneyTradeOriginalFocusCallback;
        private object moneyTradeOriginalUnfocusCallback;
        private object moneyTradeOriginalPressedCallback;
        private UIWidget localTradeReadyFrame;
        private UIWidget remoteTradeReadyFrame;
        private Color localTradeReadyFrameOriginalColor;
        private Color remoteTradeReadyFrameOriginalColor;
        private bool tradeReadyFrameLookupAttempted;
        private UILabel tradeOfferMoneyLabel;
        private UILabel.Overflow tradeOfferMoneyOriginalOverflow;
        private UI2DSprite moneyPickerIcon;
        private int moneyPickerIconOriginalWidth;
        private int moneyPickerIconOriginalHeight;
        private SmartSlider moneyPickerSlider;
        private bool moneyPickerSliderOriginalAutoSteps;
        private UISlider moneyPickerTrack;
        private UIInput moneyPickerInput;
        private int moneyPickerInputOriginalCharacterLimit;
        private UIInput.Validation moneyPickerInputOriginalValidation;
        private BoxCollider2D moneyPickerExpandedCollider;
        private UILabel moneyPickerMinCounter;
        private UILabel moneyPickerMaxCounter;
        private string moneyPickerMinCounterOriginalText;
        private string moneyPickerMaxCounterOriginalText;

        private WorldGameObject promptTarget;
        private CSteamID promptTargetSteamId = CSteamID.Nil;
        private bool promptWasForGamepad;

        public bool IsLocalBusy => localSessionId != 0 || incomingInviteVisible;
        internal bool IsTradeInProgress => tradeBegun;

        private bool IsHost => OnlineCoopManager.Instance?.IsHost == true;

        private void Awake()
        {
            if (instance != null && instance != this)
            {
                Destroy(gameObject);
                return;
            }

            instance = this;
            DontDestroyOnLoad(gameObject);
        }

        private void OnDestroy()
        {
            if (instance == this)
                instance = null;
        }

        private void Update()
        {
            OnlineCoopManager online = OnlineCoopManager.Instance;
            if (online == null || !online.IsOnlineCoopEnabled || !MainGame.game_started)
            {
                ClearInteractionPrompt();
                return;
            }

            float now = Time.realtimeSinceStartup;
            if (IsHost)
                TickHostTrades(now);

            if (localSessionId != 0)
            {
                float timeout = tradeBegun ? ActiveTimeoutSeconds : InviteTimeoutSeconds;
                if (now - localSessionLastActivityAt > timeout)
                    CancelLocalTrade("Trade timed out.", true);
            }
        }

        public bool TrySelectInteractionTarget(
            InteractionComponent interaction,
            out WorldGameObject target,
            out CSteamID targetSteamId)
        {
            target = null;
            targetSteamId = CSteamID.Nil;
            if (!CanOfferTrade(interaction))
            {
                ClearInteractionPrompt();
                return false;
            }

            Vector3 localPosition = interaction.wgo.transform.position;
            float closestSqr = InteractionDistance * InteractionDistance;
            CSteamID closestId = CSteamID.Nil;
            List<KeyValuePair<CSteamID, PlayerComponent>> remotes =
                OnlineCoopManager.Instance.GetRemotePlayersSnapshot();

            for (int i = 0; i < remotes.Count; i++)
            {
                if (GameTimeSync.Instance?.IsPeerSleeping(remotes[i].Key) == true)
                    continue;

                WorldGameObject candidate = remotes[i].Value?.wgo;
                if (candidate == null || !candidate.gameObject.activeInHierarchy)
                    continue;

                Vector3 delta = candidate.transform.position - localPosition;
                float sqr = delta.x * delta.x + delta.y * delta.y;
                if (sqr > closestSqr)
                    continue;

                closestSqr = sqr;
                closestId = remotes[i].Key;
                target = candidate;
            }

            if (target == null)
            {
                ClearInteractionPrompt();
                return false;
            }

            targetSteamId = closestId;
            return true;
        }

        public bool TryHandleInteraction(InteractionComponent interaction)
        {
            if (interaction?.wgo != MainGame.me?.player ||
                promptTarget == null || interaction.nearest != promptTarget ||
                promptTargetSteamId == CSteamID.Nil)
            {
                return false;
            }

            // A roster update can arrive between displaying the prompt and the
            // interaction key press. Consume that stale player interaction so it
            // cannot fall through to WorldGameObject.Interact as the player sleeps.
            if (GameTimeSync.Instance?.IsPeerSleeping(promptTargetSteamId) == true)
            {
                ClearInteractionPrompt();
                return true;
            }

            if (!CanOfferTrade(interaction) ||
                !IsPromptTargetStillValid(interaction.wgo))
            {
                return false;
            }

            CSteamID target = promptTargetSteamId;
            CoopMod.Logger.LogInfo(
                $"[Trade] Interaction key accepted for {GetName(target)}");
            ClearInteractionPrompt();
            RequestTrade(target);
            return true;
        }

        private bool IsPromptTargetStillValid(WorldGameObject localPlayer)
        {
            if (localPlayer == null || promptTarget == null ||
                !promptTarget.gameObject.activeInHierarchy ||
                OnlineCoopManager.Instance?.GetRemotePlayer(promptTargetSteamId) != promptTarget)
            {
                return false;
            }

            Vector3 delta = promptTarget.transform.position - localPlayer.transform.position;
            float distanceSqr = delta.x * delta.x + delta.y * delta.y;
            return distanceSqr <= InteractionDistance * InteractionDistance;
        }

        private bool CanOfferTrade(InteractionComponent interaction)
        {
            OnlineCoopManager online = OnlineCoopManager.Instance;
            BaseCharacterComponent character = interaction?.wgo?.components?.character;
            return online != null && online.IsOnlineCoopEnabled &&
                   interaction.wgo == MainGame.me?.player &&
                   character != null && character.control_enabled &&
                   (character.average_step <= 0.01f ||
                    LazyInput.GetDirection().sqrMagnitude <= 0f) &&
                   BaseGUI.all_guis_closed && !IsLocalBusy &&
                   MainGame.me?.build_mode_logics?.IsBuilding() != true;
        }

        public void ShowInteractionPrompt(WorldGameObject target, CSteamID steamId)
        {
            if (promptTarget != null && promptTarget != target)
                promptTarget.SetBubbleWidgetData((string)null, BubbleWidgetData.WidgetID.Interaction);

            bool gamepad = LazyInput.gamepad_active;
            if (promptTarget == target && promptTargetSteamId == steamId &&
                promptWasForGamepad == gamepad)
            {
                return;
            }

            promptTarget = target;
            promptTargetSteamId = steamId;
            promptWasForGamepad = gamepad;
            Vector3 delta = target.transform.position - MainGame.me.player.transform.position;
            CoopMod.Logger.LogInfo(
                $"[Trade] Interaction prompt shown for {GetName(steamId)} " +
                $"(distance={Mathf.Sqrt(delta.x * delta.x + delta.y * delta.y):F1})");
            string text = GameKeyTip.Get(
                GameKey.Interaction,
                "Trade",
                true,
                false,
                true,
                false);
            target.SetBubbleWidgetData(text, BubbleWidgetData.WidgetID.Interaction);
        }

        public bool IsInteractionPromptTarget(WorldGameObject target)
        {
            return target != null && target == promptTarget;
        }

        public void ClearInteractionPrompt()
        {
            WorldGameObject previousTarget = promptTarget;
            if (previousTarget != null)
            {
                previousTarget.SetBubbleWidgetData(
                    (string)null,
                    BubbleWidgetData.WidgetID.Interaction);

                InteractionComponent interaction =
                    MainGame.me?.player?.components?.interaction;
                if (interaction?.nearest == previousTarget)
                    interaction.nearest = null;
            }

            promptTarget = null;
            promptTargetSteamId = CSteamID.Nil;
            promptWasForGamepad = false;
        }

        private void RequestTrade(CSteamID target)
        {
            if (IsLocalBusy || target == CSteamID.Nil ||
                OnlineCoopManager.Instance?.IsRemotePlayer(target) != true)
            {
                return;
            }

            localSessionId = CreateSessionId();
            localPartner = target;
            tradeBegun = false;
            localSessionLastActivityAt = Time.realtimeSinceStartup;

            CoopMod.Logger.LogInfo(
                $"[Trade] Inviting {GetName(target)} to trade (session {localSessionId})");
            GraveyardKeeperCoop.Utils.ChatManager.AddMessage(
                $"[System] Trade invitation sent to {GetName(target)}.");

            if (IsHost)
                HostHandleRequest(
                    SteamUser.GetSteamID(),
                    target,
                    localSessionId,
                    TradeProtocolVersion);
            else
                SendRequest(target, localSessionId);
        }

        public void HandleNetworkMessage(CSteamID sender, ref MsgReader reader)
        {
            try
            {
                TradePhase phase = (TradePhase)reader.ReadByte();
                if (IsHost)
                {
                    HandleHostInbound(sender, phase, ref reader);
                    return;
                }

                CSteamID host = SteamLobbyManager.Instance?.GetLobbyOwner() ?? CSteamID.Nil;
                if (host == CSteamID.Nil || sender != host)
                {
                    CoopMod.Logger.LogWarning(
                        $"[Trade] Ignored {phase} from non-host {sender}");
                    return;
                }

                HandleClientInbound(phase, ref reader);
            }
            catch (Exception ex)
            {
                CoopMod.Logger.LogWarning($"[Trade] Rejected malformed message: {ex.Message}");
            }
        }

        private void HandleHostInbound(CSteamID sender, TradePhase phase, ref MsgReader reader)
        {
            switch (phase)
            {
                case TradePhase.Request:
                {
                    CSteamID invitee = new CSteamID(reader.ReadUInt64());
                    ulong sessionId = reader.ReadUInt64();
                    HostHandleRequest(
                        sender,
                        invitee,
                        sessionId,
                        ReadProtocolVersion(ref reader));
                    break;
                }
                case TradePhase.Accept:
                {
                    ulong sessionId = reader.ReadUInt64();
                    HostHandleAccept(
                        sender,
                        sessionId,
                        ReadProtocolVersion(ref reader));
                    break;
                }
                case TradePhase.Decline:
                    HostHandleDecline(sender, reader.ReadUInt64());
                    break;
                case TradePhase.Offer:
                {
                    ulong sessionId = reader.ReadUInt64();
                    int revision = reader.ReadInt32();
                    string inventoryJson = ReadInventorySnapshot(ref reader);
                    List<string> offer = ReadOffer(ref reader);
                    HostHandleOffer(
                        sender,
                        sessionId,
                        revision,
                        inventoryJson,
                        offer,
                        ReadMoneyOffer(ref reader));
                    break;
                }
                case TradePhase.Ready:
                    HostHandleReady(
                        sender,
                        reader.ReadUInt64(),
                        reader.ReadInt32(),
                        reader.ReadBool());
                    break;
                case TradePhase.Cancel:
                    HostHandleCancel(sender, reader.ReadUInt64(), reader.ReadString());
                    break;
                case TradePhase.CommitAck:
                    HostHandleCommitAck(sender, reader.ReadUInt64());
                    break;
            }
        }

        private void HandleClientInbound(TradePhase phase, ref MsgReader reader)
        {
            switch (phase)
            {
                case TradePhase.Invite:
                    ReceiveInvite(reader.ReadUInt64(), new CSteamID(reader.ReadUInt64()));
                    break;
                case TradePhase.Begin:
                {
                    ulong sessionId = reader.ReadUInt64();
                    CSteamID partner = new CSteamID(reader.ReadUInt64());
                    ReceiveBegin(
                        sessionId,
                        partner,
                        reader.Remaining > 0 && reader.ReadBool());
                    break;
                }
                case TradePhase.Offer:
                {
                    ulong sessionId = reader.ReadUInt64();
                    int revision = reader.ReadInt32();
                    string inventoryJson = ReadInventorySnapshot(ref reader);
                    List<string> offer = ReadOffer(ref reader);
                    ReceiveRemoteOffer(
                        sessionId,
                        revision,
                        inventoryJson,
                        offer,
                        ReadMoneyOffer(ref reader));
                    break;
                }
                case TradePhase.Ready:
                    ReceiveRemoteReady(reader.ReadUInt64(), reader.ReadBool());
                    break;
                case TradePhase.Cancel:
                    ReceiveCancel(reader.ReadUInt64(), reader.ReadString());
                    break;
                case TradePhase.Commit:
                {
                    ulong sessionId = reader.ReadUInt64();
                    int expectedRevision = reader.ReadInt32();
                    List<string> incoming = ReadOffer(ref reader);
                    ReceiveCommit(
                        sessionId,
                        expectedRevision,
                        incoming,
                        ReadMoneyOffer(ref reader));
                    break;
                }
            }
        }

        private void HostHandleRequest(
            CSteamID inviter,
            CSteamID invitee,
            ulong sessionId,
            byte inviterProtocolVersion)
        {
            if (!IsCurrentLobbyMember(inviter) || !IsCurrentLobbyMember(invitee) ||
                inviter == invitee || sessionId == 0 || hostTrades.ContainsKey(sessionId))
            {
                DeliverCancel(inviter, sessionId, "Trade invitation was invalid.");
                return;
            }

            if (IsSleepingTradeParticipant(inviter) ||
                IsSleepingTradeParticipant(invitee))
            {
                DeliverCancel(
                    inviter,
                    sessionId,
                    "Sleeping players cannot trade.");
                return;
            }

            if (FindHostTradeFor(inviter) != null || FindHostTradeFor(invitee) != null)
            {
                DeliverCancel(inviter, sessionId, $"{GetName(invitee)} is already trading.");
                return;
            }

            if (!ArePlayersNear(inviter, invitee, InteractionDistance * 1.5f))
            {
                DeliverCancel(inviter, sessionId, "That player is too far away to trade.");
                return;
            }

            var trade = new HostTrade
            {
                SessionId = sessionId,
                Inviter = inviter,
                Invitee = invitee,
                InviterProtocolVersion = inviterProtocolVersion,
                LastActivityAt = Time.realtimeSinceStartup
            };
            hostTrades[sessionId] = trade;
            DeliverInvite(invitee, sessionId, inviter);
        }

        private void HostHandleAccept(
            CSteamID sender,
            ulong sessionId,
            byte inviteeProtocolVersion)
        {
            if (!hostTrades.TryGetValue(sessionId, out HostTrade trade) ||
                trade.Invitee != sender || trade.Begun)
            {
                return;
            }

            if (IsSleepingTradeParticipant(trade.Inviter) ||
                IsSleepingTradeParticipant(trade.Invitee))
            {
                CancelHostTrade(trade, "Sleeping players cannot trade.");
                return;
            }

            if (!ArePlayersNear(trade.Inviter, trade.Invitee, ActiveTradeDistance))
            {
                CancelHostTrade(trade, "Players moved too far apart.");
                return;
            }

            trade.Begun = true;
            trade.InviteeProtocolVersion = inviteeProtocolVersion;
            trade.MoneyTradingEnabled =
                trade.InviterProtocolVersion >= TradeProtocolVersion &&
                trade.InviteeProtocolVersion >= TradeProtocolVersion;
            trade.LastActivityAt = Time.realtimeSinceStartup;

            // Opening the host's local screen immediately publishes its initial
            // offer. Open the network participant first so that offer cannot
            // overtake their Begin message and get discarded as pre-session data.
            CSteamID local = SteamUser.GetSteamID();
            if (trade.Inviter == local)
            {
                DeliverBegin(
                    trade.Invitee,
                    sessionId,
                    trade.Inviter,
                    trade.MoneyTradingEnabled);
                DeliverBegin(
                    trade.Inviter,
                    sessionId,
                    trade.Invitee,
                    trade.MoneyTradingEnabled);
            }
            else
            {
                DeliverBegin(
                    trade.Inviter,
                    sessionId,
                    trade.Invitee,
                    trade.MoneyTradingEnabled);
                DeliverBegin(
                    trade.Invitee,
                    sessionId,
                    trade.Inviter,
                    trade.MoneyTradingEnabled);
            }
        }

        private static bool IsSleepingTradeParticipant(CSteamID player)
        {
            return GameTimeSync.Instance?.IsPeerSleeping(player) == true;
        }

        private void HostHandleDecline(CSteamID sender, ulong sessionId)
        {
            if (!hostTrades.TryGetValue(sessionId, out HostTrade trade) ||
                trade.Invitee != sender || trade.Begun)
            {
                return;
            }

            CancelHostTrade(trade, $"{GetName(sender)} declined the trade invitation.");
        }

        private void HostHandleOffer(
            CSteamID sender,
            ulong sessionId,
            int revision,
            string inventoryJson,
            List<string> offer,
            int moneyOfferBronze)
        {
            if (!TryGetBegunHostTrade(sender, sessionId, out HostTrade trade) ||
                revision < 0 || !ValidateInventorySnapshot(inventoryJson) ||
                !ValidateOffer(offer) || !ValidateMoneyOffer(moneyOfferBronze) ||
                (!trade.MoneyTradingEnabled && moneyOfferBronze != 0))
            {
                return;
            }

            if (sender == trade.Inviter)
            {
                if (revision <= trade.InviterRevision)
                    return;
                trade.InviterRevision = revision;
                trade.InviterOffer = offer;
                trade.InviterMoneyOfferBronze = moneyOfferBronze;
                trade.InviterInventoryJson = inventoryJson;
            }
            else
            {
                if (revision <= trade.InviteeRevision)
                    return;
                trade.InviteeRevision = revision;
                trade.InviteeOffer = offer;
                trade.InviteeMoneyOfferBronze = moneyOfferBronze;
                trade.InviteeInventoryJson = inventoryJson;
            }

            trade.InviterReady = false;
            trade.InviteeReady = false;
            trade.LastActivityAt = Time.realtimeSinceStartup;
            CSteamID other = sender == trade.Inviter ? trade.Invitee : trade.Inviter;
            DeliverOffer(
                other,
                sessionId,
                revision,
                inventoryJson,
                offer,
                moneyOfferBronze);
            DeliverReady(sender, sessionId, false);
            DeliverReady(other, sessionId, false);
        }

        private void HostHandleReady(
            CSteamID sender,
            ulong sessionId,
            int revision,
            bool ready)
        {
            if (!TryGetBegunHostTrade(sender, sessionId, out HostTrade trade))
                return;

            if (sender == trade.Inviter)
            {
                if (revision != trade.InviterRevision)
                    return;
                trade.InviterReady = ready;
            }
            else
            {
                if (revision != trade.InviteeRevision)
                    return;
                trade.InviteeReady = ready;
            }

            trade.LastActivityAt = Time.realtimeSinceStartup;
            CSteamID other = sender == trade.Inviter ? trade.Invitee : trade.Inviter;
            DeliverReady(other, sessionId, ready);

            if (!trade.InviterReady || !trade.InviteeReady)
                return;

            if (trade.InviterOffer.Count == 0 && trade.InviteeOffer.Count == 0 &&
                trade.InviterMoneyOfferBronze == 0 &&
                trade.InviteeMoneyOfferBronze == 0)
            {
                trade.InviterReady = false;
                trade.InviteeReady = false;
                DeliverReady(trade.Inviter, sessionId, false);
                DeliverReady(trade.Invitee, sessionId, false);
                return;
            }

            if (!ArePlayersNear(trade.Inviter, trade.Invitee, ActiveTradeDistance))
            {
                CancelHostTrade(trade, "Players moved too far apart.");
                return;
            }

            trade.Committed = true;
            trade.NextCommitSendAt = Time.realtimeSinceStartup + 2f;
            trade.LastActivityAt = Time.realtimeSinceStartup;
            DeliverCommit(
                trade.Inviter,
                sessionId,
                trade.InviterRevision,
                trade.InviteeOffer,
                trade.InviteeMoneyOfferBronze);
            DeliverCommit(
                trade.Invitee,
                sessionId,
                trade.InviteeRevision,
                trade.InviterOffer,
                trade.InviterMoneyOfferBronze);
        }

        private void HostHandleCommitAck(CSteamID sender, ulong sessionId)
        {
            if (!hostTrades.TryGetValue(sessionId, out HostTrade trade) ||
                !trade.Committed)
            {
                return;
            }

            if (sender == trade.Inviter)
                trade.InviterCommitAck = true;
            else if (sender == trade.Invitee)
                trade.InviteeCommitAck = true;
            else
                return;

            if (trade.InviterCommitAck && trade.InviteeCommitAck)
            {
                hostTrades.Remove(sessionId);
                CoopMod.Logger.LogInfo(
                    $"[Trade] Commit acknowledged by both players (session {sessionId})");
            }
        }

        private void HostHandleCancel(CSteamID sender, ulong sessionId, string reason)
        {
            if (!hostTrades.TryGetValue(sessionId, out HostTrade trade) ||
                (trade.Inviter != sender && trade.Invitee != sender))
            {
                return;
            }
            CancelHostTrade(
                trade,
                string.IsNullOrEmpty(reason) ? $"{GetName(sender)} cancelled the trade." : reason);
        }

        private void TickHostTrades(float now)
        {
            expiredHostTrades.Clear();
            foreach (KeyValuePair<ulong, HostTrade> pair in hostTrades)
            {
                HostTrade trade = pair.Value;
                if (trade.Committed)
                {
                    if (now >= trade.NextCommitSendAt)
                    {
                        trade.NextCommitSendAt = now + 2f;
                        if (!trade.InviterCommitAck)
                        {
                            DeliverCommit(
                                trade.Inviter,
                                trade.SessionId,
                                trade.InviterRevision,
                                trade.InviteeOffer,
                                trade.InviteeMoneyOfferBronze);
                        }
                        if (!trade.InviteeCommitAck)
                        {
                            DeliverCommit(
                                trade.Invitee,
                                trade.SessionId,
                                trade.InviteeRevision,
                                trade.InviterOffer,
                                trade.InviterMoneyOfferBronze);
                        }
                    }

                    if (now - trade.LastActivityAt > InviteTimeoutSeconds)
                    {
                        CoopMod.Logger.LogWarning(
                            $"[Trade] Commit acknowledgement timed out for session {trade.SessionId}");
                        expiredHostTrades.Add(pair.Key);
                    }
                    continue;
                }

                float timeout = trade.Begun ? ActiveTimeoutSeconds : InviteTimeoutSeconds;
                if (now - trade.LastActivityAt > timeout ||
                    !IsCurrentLobbyMember(trade.Inviter) ||
                    !IsCurrentLobbyMember(trade.Invitee) ||
                    IsSleepingTradeParticipant(trade.Inviter) ||
                    IsSleepingTradeParticipant(trade.Invitee) ||
                    (trade.Begun && !ArePlayersNear(
                        trade.Inviter,
                        trade.Invitee,
                        ActiveTradeDistance)))
                {
                    expiredHostTrades.Add(pair.Key);
                }
            }

            for (int i = 0; i < expiredHostTrades.Count; i++)
            {
                if (hostTrades.TryGetValue(expiredHostTrades[i], out HostTrade trade))
                {
                    if (trade.Committed)
                        hostTrades.Remove(trade.SessionId);
                    else
                    {
                        string reason =
                            IsSleepingTradeParticipant(trade.Inviter) ||
                            IsSleepingTradeParticipant(trade.Invitee)
                                ? "Sleeping players cannot trade."
                                : "Trade ended because a player left or moved away.";
                        CancelHostTrade(trade, reason);
                    }
                }
            }
        }

        private void CancelHostTrade(HostTrade trade, string reason)
        {
            if (trade == null || !hostTrades.Remove(trade.SessionId))
                return;
            DeliverCancel(trade.Inviter, trade.SessionId, reason);
            DeliverCancel(trade.Invitee, trade.SessionId, reason);
        }

        private HostTrade FindHostTradeFor(CSteamID player)
        {
            foreach (HostTrade trade in hostTrades.Values)
            {
                if (trade.Inviter == player || trade.Invitee == player)
                    return trade;
            }
            return null;
        }

        private bool TryGetBegunHostTrade(
            CSteamID sender,
            ulong sessionId,
            out HostTrade trade)
        {
            return hostTrades.TryGetValue(sessionId, out trade) && trade.Begun &&
                   (trade.Inviter == sender || trade.Invitee == sender);
        }

        private void ReceiveInvite(ulong sessionId, CSteamID inviter)
        {
            if (sessionId == 0 || inviter == CSteamID.Nil)
                return;

            BaseCharacterComponent localCharacter =
                MainGame.me?.player?.components?.character;
            if (IsLocalBusy || !MainGame.game_started ||
                !BaseGUI.all_guis_closed || localCharacter == null ||
                !localCharacter.control_enabled)
            {
                SendParticipantPhase(TradePhase.Decline, sessionId);
                return;
            }

            localSessionId = sessionId;
            localPartner = inviter;
            incomingInviteVisible = true;
            localSessionLastActivityAt = Time.realtimeSinceStartup;
            string name = GetName(inviter);

            if (GUIElements.me?.dialog == null)
            {
                DeclineIncomingInvite();
                return;
            }

            GUIElements.me.dialog.Open(
                $"{name} wants to trade with you.",
                "Accept",
                AcceptIncomingInvite,
                "Decline",
                DeclineIncomingInvite,
                null,
                GameKey.Select,
                GameKey.Back);
        }

        private void AcceptIncomingInvite()
        {
            if (!incomingInviteVisible || localSessionId == 0)
                return;
            incomingInviteVisible = false;
            SendParticipantPhase(TradePhase.Accept, localSessionId);
        }

        private void DeclineIncomingInvite()
        {
            if (!incomingInviteVisible || localSessionId == 0)
                return;
            ulong sessionId = localSessionId;
            incomingInviteVisible = false;
            SendParticipantPhase(TradePhase.Decline, sessionId);
            ResetLocalState();
        }

        private void ReceiveBegin(
            ulong sessionId,
            CSteamID partner,
            bool enableMoneyTrading)
        {
            if (sessionId != localSessionId || partner != localPartner)
                return;

            BaseCharacterComponent localCharacter =
                MainGame.me?.player?.components?.character;
            if (!BaseGUI.all_guis_closed || localCharacter == null ||
                !localCharacter.control_enabled)
            {
                CancelLocalTrade(
                    "Trade cancelled because a player became busy.",
                    true);
                return;
            }

            incomingInviteVisible = false;
            tradeBegun = true;
            localReady = false;
            remoteReady = false;
            localRevision = 0;
            remoteRevision = 0;
            localMoneyOfferBronze = 0;
            remoteMoneyOfferBronze = 0;
            moneyTradingEnabled = enableMoneyTrading;
            localSessionLastActivityAt = Time.realtimeSinceStartup;
            OpenTradeGui();
        }

        private void ReceiveRemoteOffer(
            ulong sessionId,
            int revision,
            string inventoryJson,
            List<string> offer,
            int moneyOfferBronze)
        {
            if (!tradeBegun || sessionId != localSessionId ||
                revision <= remoteRevision ||
                !ValidateInventorySnapshot(inventoryJson) || !ValidateOffer(offer) ||
                !ValidateMoneyOffer(moneyOfferBronze) ||
                (!moneyTradingEnabled && moneyOfferBronze != 0))
            {
                return;
            }

            remoteRevision = revision;
            remoteReady = false;
            localReady = false;
            localSessionLastActivityAt = Time.realtimeSinceStartup;
            ApplyRemoteInventorySnapshot(inventoryJson);
            ReplaceOfferRoot(remoteOfferRoot, offer);
            remoteMoneyOfferBronze = moneyOfferBronze;
            RedrawTradeGui();
        }

        private void ReceiveRemoteReady(ulong sessionId, bool ready)
        {
            if (!tradeBegun || sessionId != localSessionId)
                return;
            remoteReady = ready;
            localSessionLastActivityAt = Time.realtimeSinceStartup;
            CoopMod.Logger.LogInfo(
                $"[Trade] Remote ready state changed to {remoteReady} " +
                $"(session {localSessionId})");
            RedrawTradeGui();
        }

        private void ReceiveCancel(ulong sessionId, string reason)
        {
            if (sessionId == 0 || sessionId != localSessionId)
                return;

            bool wasOpen = tradeBegun;
            if (incomingInviteVisible && GUIElements.me?.dialog?.is_shown == true)
                GUIElements.me.dialog.Hide(true);
            CloseTradeGui(commit: false);
            ResetLocalState();
            if (!string.IsNullOrEmpty(reason))
            {
                GraveyardKeeperCoop.Utils.ChatManager.AddMessage($"[System] {reason}");
                if (wasOpen && GUIElements.me?.dialog != null)
                    GUIElements.me.dialog.OpenOK(reason);
            }
        }

        private void ReceiveCommit(
            ulong sessionId,
            int expectedRevision,
            List<string> incoming,
            int incomingMoneyBronze)
        {
            if (completedSessions.Contains(sessionId))
            {
                SendCommitAck(sessionId);
                return;
            }

            if (!tradeBegun || sessionId != localSessionId ||
                expectedRevision != localRevision || !ValidateOffer(incoming) ||
                !ValidateMoneyOffer(incomingMoneyBronze) ||
                (!moneyTradingEnabled && incomingMoneyBronze != 0))
            {
                return;
            }

            completedSessions.Add(sessionId);
            SendCommitAck(sessionId);
            committingTrade = true;
            localOfferRoot.inventory.Clear();
            localMoneyOfferBronze = 0;
            GiveItemsToLocalPlayer(incoming);
            GiveMoneyToLocalPlayer(incomingMoneyBronze);
            string partnerName = GetName(localPartner);
            CloseTradeGui(commit: true);
            ResetLocalState();
            GraveyardKeeperCoop.Utils.ChatManager.AddMessage(
                $"[System] Trade with {partnerName} completed.");
            committingTrade = false;
        }

        public void CancelLocalTrade(string reason, bool notifyHost)
        {
            if (localSessionId == 0)
                return;
            ulong sessionId = localSessionId;
            if (notifyHost)
            {
                SendCancel(sessionId, reason);
                // The host routes its own cancellation synchronously and may
                // already have closed/reset this participant through ReceiveCancel.
                if (localSessionId != sessionId)
                    return;
            }
            if (incomingInviteVisible && GUIElements.me?.dialog?.is_shown == true)
                GUIElements.me.dialog.Hide(true);
            CloseTradeGui(commit: false);
            ResetLocalState();
            if (!string.IsNullOrEmpty(reason))
                GraveyardKeeperCoop.Utils.ChatManager.AddMessage($"[System] {reason}");
        }

        public void NotifyPeerLeft(CSteamID peer)
        {
            if (peer == CSteamID.Nil)
                return;

            if (localPartner == peer)
                CancelLocalTrade("Trade ended because the other player left.", false);

            if (!IsHost)
                return;
            HostTrade trade = FindHostTradeFor(peer);
            if (trade != null)
                CancelHostTrade(trade, "Trade ended because a player left.");
        }

        public void Shutdown()
        {
            ClearInteractionPrompt();
            CloseTradeGui(commit: false);
            ResetLocalState();
            hostTrades.Clear();
            completedSessions.Clear();
        }

        private void OpenTradeGui()
        {
            try
            {
                if (GUIElements.me?.vendor == null || MainGame.me?.player == null)
                    throw new InvalidOperationException("Trade UI is not available.");

                tradeGui = GUIElements.me.vendor;
                localInventory = MainGame.me.player.GetMultiInventory(
                    null,
                    "",
                    MultiInventory.PlayerMultiInventory.DontChange,
                    false,
                    true,
                    true);

                localOfferRoot = CreateOfferRoot();
                remoteOfferRoot = CreateOfferRoot();
                remoteInventoryRoot = CreateRemoteInventoryRoot();
                remoteInventory = BuildRemoteInventory(
                    remoteInventoryRoot,
                    GetName(localPartner));
                localOfferInventory = new MultiInventory(
                    new Inventory(localOfferRoot, ">>> Your offer >>>", ""),
                    null,
                    null);
                remoteOfferInventory = new MultiInventory(
                    new Inventory(remoteOfferRoot, $"{GetName(localPartner)}'s offer", ""),
                    null,
                    null);

                ((BaseGUI)tradeGui).Open();
                tradeGui.SetOnHide(null, true);
                tradeGui.trading = null;
                PlayerField.SetValue(tradeGui, localInventory);
                VendorField.SetValue(tradeGui, remoteInventory);
                VendorRealField.SetValue(tradeGui, remoteInventory);
                PlayerOfferField.SetValue(tradeGui, localOfferInventory);
                VendorOfferField.SetValue(tradeGui, remoteOfferInventory);
                VendorObjectField.SetValue(tradeGui, null);
                EnableTimeAfterCloseField?.SetValue(
                    tradeGui,
                    EnvironmentEngine.me != null && EnvironmentEngine.me.auto_adjust_time);
                SelectedPanelField.SetValue(tradeGui, null);
                SelectedWidgetField.SetValue(tradeGui, null);
                SelectedItemGuiField.SetValue(tradeGui, null);
                SelectedItemField.SetValue(tradeGui, null);

                tradeGui.player_panel.Open(localInventory, 1, 0, false, -1, false);
                tradeGui.vendor_panel.Open(remoteInventory, 2, 0, true, -1, false);
                tradeGui.player_panel.SetGrayToNotMainWidgets(true);
                tradeGui.player_offer_widget.Open(
                    localOfferInventory.all[0],
                    BaseGUI.for_gamepad,
                    3,
                    0,
                    false,
                    -1);
                tradeGui.vendor_offer_widget.Open(
                    remoteOfferInventory.all[0],
                    BaseGUI.for_gamepad,
                    3,
                    1,
                    false,
                    -1);

                tradeGui.player_panel.panel_title.text = "Your inventory";
                tradeGui.vendor_panel.panel_title.text =
                    $"{GetName(localPartner)}'s inventory";
                ApplyTradePanelHeads();
                ConfigureMoneyInteraction();

                RedrawTradeGui();
                if (BaseGUI.for_gamepad && tradeGui.gamepad_controller != null)
                {
                    tradeGui.gamepad_controller.ReinitItems(false);
                    tradeGui.gamepad_controller.FocusOnFirstActive(-1);
                }

                SendLocalOffer();
                CoopMod.Logger.LogInfo(
                    $"[Trade] Opened trade with {GetName(localPartner)} (session {localSessionId})");
            }
            catch (Exception ex)
            {
                CoopMod.Logger.LogError($"[Trade] Could not open trade UI: {ex}");
                CancelLocalTrade("Trade could not be opened.", true);
            }
        }

        private void CloseTradeGui(bool commit)
        {
            if (tradeGui == null)
            {
                if (!commit)
                {
                    ReturnEscrowToInventory();
                    ReturnMoneyEscrow();
                }
                return;
            }

            closingTradeGui = true;
            committingTrade = commit;
            try
            {
                if (!commit)
                {
                    ReturnEscrowToInventory();
                    ReturnMoneyEscrow();
                }
                RemoveMoneyInteraction();
                if (GUIElements.me?.item_count?.is_shown == true)
                    GUIElements.me.item_count.Hide(true);
                RemoveTradeReadyIndicators();
                RestoreTradeOfferMoneyLabel();
                if (tradeGui.is_shown)
                    tradeGui.Hide(true);
            }
            catch (Exception ex)
            {
                CoopMod.Logger.LogWarning($"[Trade] Failed while closing trade UI: {ex.Message}");
            }
            finally
            {
                closingTradeGui = false;
                committingTrade = false;
                tradeGui = null;
                localInventory = null;
                remoteInventory = null;
                localOfferInventory = null;
                remoteOfferInventory = null;
                remoteInventoryRoot = null;
                localOfferRoot = null;
                remoteOfferRoot = null;
            }
        }

        private void ResetLocalState()
        {
            localSessionId = 0;
            localPartner = CSteamID.Nil;
            incomingInviteVisible = false;
            tradeBegun = false;
            localReady = false;
            remoteReady = false;
            localRevision = 0;
            remoteRevision = 0;
            localMoneyOfferBronze = 0;
            remoteMoneyOfferBronze = 0;
            moneyTradingEnabled = false;
            localSessionLastActivityAt = 0f;
        }

        public bool OwnsGui(VendorGUI gui)
        {
            return gui != null && gui == tradeGui && tradeBegun;
        }

        public void PatchMoveItem(VendorGUI gui, int count)
        {
            if (!OwnsGui(gui) || localInventory == null || localOfferInventory == null)
                return;

            InventoryPanelGUI panel = SelectedPanelField.GetValue(gui) as InventoryPanelGUI;
            InventoryWidget widget = SelectedWidgetField.GetValue(gui) as InventoryWidget;
            BaseItemCellGUI itemGui = SelectedItemGuiField.GetValue(gui) as BaseItemCellGUI;
            Item item = SelectedItemField.GetValue(gui) as Item;
            if (item == null || item.IsEmpty() || itemGui == null || itemGui.is_inactive_state)
                return;

            MultiInventory from;
            MultiInventory to;
            if (panel == gui.player_panel)
            {
                from = localInventory;
                to = localOfferInventory;
            }
            else if (widget == gui.player_offer_widget)
            {
                from = localOfferInventory;
                to = localInventory;
            }
            else
            {
                return;
            }

            int moveCount = count <= 0 ? item.value : Mathf.Min(count, item.value);
            if (moveCount <= 0 || !from.MoveItemTo(to, item, moveCount, false, true))
            {
                return;
            }

            Sounds.PlaySound("item_put", null, false, 0f);
            SendLocalOffer();
            RedrawTradeGui();
            TooltipsManager.Redraw();
        }

        public bool PatchUpdateTips(VendorGUI gui)
        {
            if (!OwnsGui(gui))
                return true;

            InventoryPanelGUI panel =
                SelectedPanelField.GetValue(gui) as InventoryPanelGUI;
            InventoryWidget widget =
                SelectedWidgetField.GetValue(gui) as InventoryWidget;
            bool remoteSelection = panel == gui.vendor_panel ||
                                   widget == gui.vendor_offer_widget;
            if (!remoteSelection)
                return true;

            gui.button_tips.Print(
                new List<GameKeyTip>
                {
                    GameKeyTip.Option2(
                        localReady ? "not ready" : "ready",
                        true,
                        true,
                        true),
                    GameKeyTip.Close(true, true, true)
                },
                "   ");
            return false;
        }

        public void PatchFinishOffer()
        {
            if (!tradeBegun)
                return;
            if (localOfferRoot.inventory.Count == 0 && remoteOfferRoot.inventory.Count == 0 &&
                localMoneyOfferBronze == 0 && remoteMoneyOfferBronze == 0)
            {
                GUIElements.me?.dialog?.OpenOK("At least one player must offer an item or money.");
                return;
            }

            localReady = !localReady;
            localSessionLastActivityAt = Time.realtimeSinceStartup;
            CoopMod.Logger.LogInfo(
                $"[Trade] Local ready state changed to {localReady} " +
                $"(session {localSessionId})");
            SendReady(localSessionId, localRevision, localReady);
            RedrawTradeGui();
        }

        public void PatchResetOrder()
        {
            if (!tradeBegun || committingTrade)
                return;
            ReturnEscrowToInventory();
            ReturnMoneyEscrow();
            if (!closingTradeGui)
            {
                SendLocalOffer();
                RedrawTradeGui();
            }
        }

        public void PatchClose()
        {
            if (!tradeBegun)
                return;

            if (GUIElements.me?.dialog == null)
            {
                CancelLocalTrade("Trade cancelled.", true);
                return;
            }

            GUIElements.me.dialog.Open(
                "Cancel this trade?",
                "Cancel trade",
                delegate { CancelLocalTrade("Trade cancelled.", true); },
                "Keep trading",
                delegate { RedrawTradeGui(); },
                null,
                GameKey.Select,
                GameKey.Back);
        }

        public bool PatchOfferIsEmpty()
        {
            return localOfferRoot == null || remoteOfferRoot == null ||
                   (localOfferRoot.inventory.Count == 0 && remoteOfferRoot.inventory.Count == 0 &&
                    localMoneyOfferBronze == 0 && remoteMoneyOfferBronze == 0);
        }

        public void RedrawTradeGui()
        {
            if (tradeGui == null || !tradeGui.is_shown)
                return;

            tradeGui.player_panel.Redraw();
            tradeGui.vendor_panel.Redraw();
            tradeGui.player_offer_widget.Redraw();
            tradeGui.vendor_offer_widget.Redraw();
            DrawTradeBalances();
            ApplyTradePanelHeads();
            UpdateTradeReadyIndicators();
            if (tradeGui.result_money != null)
            {
                ConfigureTradeOfferMoneyLabel(tradeGui.result_money);
                string local = FormatTradeMoneyOffer(localMoneyOfferBronze);
                string remote = FormatTradeMoneyOffer(remoteMoneyOfferBronze);
                tradeGui.result_money.text = local + "   |   " + remote;
            }

            SetButtonLabel(tradeGui.btn_confirm, localReady ? "Not ready" : "Ready");
            SetButtonLabel(tradeGui.btn_cancel, "Clear offer");
            SetButtonEnabled(tradeGui.btn_confirm, true);
            SetButtonEnabled(
                tradeGui.btn_cancel,
                (localOfferRoot != null && localOfferRoot.inventory.Count > 0) ||
                localMoneyOfferBronze > 0);
        }

        private void SendLocalOffer()
        {
            if (!tradeBegun || localOfferRoot == null)
                return;
            localReady = false;
            remoteReady = false;
            localRevision++;
            localSessionLastActivityAt = Time.realtimeSinceStartup;
            List<string> offer = SerializeOffer(localOfferRoot);
            string inventoryJson = SerializeLocalInventorySnapshot();
            if (string.IsNullOrEmpty(inventoryJson))
            {
                CancelLocalTrade(
                    "Trade cancelled because the inventory snapshot was too large.",
                    true);
                return;
            }
            if (IsHost)
            {
                HostHandleOffer(
                    SteamUser.GetSteamID(),
                    localSessionId,
                    localRevision,
                    inventoryJson,
                    offer,
                    localMoneyOfferBronze);
            }
            else
                SendOffer(
                    localSessionId,
                    localRevision,
                    inventoryJson,
                    offer,
                    localMoneyOfferBronze);
        }

        private void ReturnEscrowToInventory()
        {
            if (localOfferRoot?.inventory == null || localInventory == null ||
                localOfferRoot.inventory.Count == 0)
            {
                return;
            }

            var returning = new List<Item>(localOfferRoot.inventory);
            localOfferRoot.inventory.Clear();
            for (int i = 0; i < returning.Count; i++)
            {
                if (!localInventory.AddItem(returning[i]))
                    DropAtLocalPlayer(returning[i]);
            }
        }

        private void ReturnMoneyEscrow()
        {
            Item player = MainGame.me?.player?.data;
            if (player == null || localMoneyOfferBronze <= 0)
                return;

            int balanceBronze = MoneyToBronze(player.money);
            player.money = BronzeToMoney(ClampMoneyBronze(
                (long)balanceBronze + localMoneyOfferBronze));
            localMoneyOfferBronze = 0;
        }

        private void GiveItemsToLocalPlayer(List<string> itemJson)
        {
            for (int i = 0; i < itemJson.Count; i++)
            {
                Item item = DeserializeItem(itemJson[i]);
                if (item == null)
                    continue;
                if (!localInventory.AddItem(item))
                    DropAtLocalPlayer(item);
            }
        }

        private static void GiveMoneyToLocalPlayer(int moneyBronze)
        {
            Item player = MainGame.me?.player?.data;
            if (player == null || moneyBronze <= 0)
                return;

            int balanceBronze = MoneyToBronze(player.money);
            player.money = BronzeToMoney(ClampMoneyBronze(
                (long)balanceBronze + moneyBronze));
            DropCollectGUI.OnMoneyCollected(BronzeToMoney(moneyBronze));
        }

        private static void DropAtLocalPlayer(Item item)
        {
            if (item == null || item.IsEmpty() || MainGame.me?.player == null)
                return;
            DropResGameObject.Drop(
                MainGame.me.player.transform.position,
                item,
                MainGame.me.world_root,
                Direction.None);
        }

        private static Item CreateOfferRoot()
        {
            var root = new Item();
            root.inventory.Clear();
            root.SetInventorySize(OfferSlots);
            return root;
        }

        private static Item CreateRemoteInventoryRoot()
        {
            var root = new Item("player", 1);
            root.inventory.Clear();
            root.SetInventorySize(20);
            return root;
        }

        private static MultiInventory BuildRemoteInventory(Item root, string playerName)
        {
            var inventories = new List<Inventory>
            {
                new Inventory(root, $"{playerName}'s inventory", "")
            };

            if (root?.inventory != null)
            {
                for (int i = 0; i < root.inventory.Count; i++)
                {
                    Item item = root.inventory[i];
                    if (item != null && !item.IsEmpty() && item.is_bag)
                        inventories.Add(new Inventory(item, item.id, ""));
                }
            }

            return new MultiInventory(inventories);
        }

        private string SerializeLocalInventorySnapshot()
        {
            Item root = MainGame.me?.player?.data;
            if (root == null)
                return string.Empty;

            // The trade peer only needs the visible inventory tree and balance.
            // Do not send stats, story params, or secondary inventory as part of
            // this UI snapshot.
            Item snapshot = CreateRemoteInventoryRoot();
            snapshot.SetInventorySize(root.inventory_size);
            snapshot.money = root.money;
            snapshot.inventory = root.inventory != null
                ? new List<Item>(root.inventory)
                : new List<Item>();
            string json = snapshot.ToJSON();
            return !string.IsNullOrEmpty(json) && json.Length <= MaxInventoryJsonLength
                ? json
                : string.Empty;
        }

        private void ApplyRemoteInventorySnapshot(string inventoryJson)
        {
            Item snapshot = DeserializeItem(inventoryJson);
            if (snapshot == null)
                return;

            remoteInventoryRoot = snapshot;
            remoteInventory = BuildRemoteInventory(snapshot, GetName(localPartner));
            if (tradeGui == null)
                return;

            VendorField.SetValue(tradeGui, remoteInventory);
            VendorRealField.SetValue(tradeGui, remoteInventory);
            tradeGui.vendor_panel.Hide();
            tradeGui.vendor_panel.Open(remoteInventory, 2, 0, true, -1, false);
            tradeGui.vendor_panel.panel_title.text =
                $"{GetName(localPartner)}'s inventory";
            ApplyTradePanelHeads();

            if (BaseGUI.for_gamepad && tradeGui.gamepad_controller != null)
                tradeGui.gamepad_controller.ReinitItems(true);
        }

        private void DrawTradeBalances()
        {
            if (tradeGui?.player_panel?.money_label != null)
            {
                float localMoney = MainGame.me?.player?.data?.money ?? 0f;
                Trading.DrawMoneyOnLabel(
                    tradeGui.player_panel.money_label,
                    SanitizeMoney(localMoney),
                    true);
            }

            if (tradeGui?.vendor_panel?.money_label != null)
            {
                float remoteMoney = remoteInventoryRoot?.money ?? 0f;
                Trading.DrawMoneyOnLabel(
                    tradeGui.vendor_panel.money_label,
                    SanitizeMoney(remoteMoney),
                    true);
            }
        }

        private void ConfigureMoneyInteraction()
        {
            RemoveMoneyInteraction();
            if (!moneyTradingEnabled || tradeGui?.player_panel?.money_label == null)
                return;

            moneyTradeLabel = tradeGui.player_panel.money_label;
            moneyLabelNormalColor = moneyTradeLabel.color;

            // NGUI calculates raycast depth from widgets on the collider object (or its
            // children), not from parent widgets. Put the collider on the UILabel itself so
            // the money target participates at the same depth as the visible balance.
            moneyTradeHitbox = moneyTradeLabel.gameObject;

            int width = Mathf.Max(80, moneyTradeLabel.width + 12);
            int height = Mathf.Max(24, moneyTradeLabel.height + 8);
            Vector2 pivot = NGUIMath.GetPivotOffset(moneyTradeLabel.pivot);

            moneyTradeCollider = moneyTradeHitbox.GetComponent<BoxCollider2D>();
            ownsMoneyTradeCollider = moneyTradeCollider == null;
            if (ownsMoneyTradeCollider)
            {
                moneyTradeCollider = moneyTradeHitbox.AddComponent<BoxCollider2D>();
            }
            else
            {
                moneyTradeColliderWasEnabled = moneyTradeCollider.enabled;
                moneyTradeColliderWasTrigger = moneyTradeCollider.isTrigger;
                moneyTradeColliderOriginalSize = moneyTradeCollider.size;
                moneyTradeColliderOriginalOffset = moneyTradeCollider.offset;
            }
            moneyTradeCollider.enabled = true;
            moneyTradeCollider.isTrigger = true;
            moneyTradeCollider.size = new Vector2(width, height);
            moneyTradeCollider.offset = new Vector2(
                (0.5f - pivot.x) * moneyTradeLabel.width,
                (0.5f - pivot.y) * moneyTradeLabel.height);

            moneyTradeListener = moneyTradeHitbox.GetComponent<UIEventListener>();
            ownsMoneyTradeListener = moneyTradeListener == null;
            if (ownsMoneyTradeListener)
                moneyTradeListener = UIEventListener.Get(moneyTradeHitbox);
            moneyTradeListener.onHover += OnMoneyTradeHovered;
            moneyTradeListener.onClick += OnMoneyTradeClicked;

            moneyTradeNavigation = moneyTradeHitbox.GetComponent<GamepadNavigationItem>();
            ownsMoneyTradeNavigation = moneyTradeNavigation == null;
            if (ownsMoneyTradeNavigation)
            {
                bool hadAutoScroll = moneyTradeHitbox.GetComponent<PanelAutoScroll>() != null;
                moneyTradeNavigation = moneyTradeHitbox.AddComponent<GamepadNavigationItem>();
                moneyTradeAutoScroll = moneyTradeHitbox.GetComponent<PanelAutoScroll>();
                ownsMoneyTradeAutoScroll = !hadAutoScroll && moneyTradeAutoScroll != null;
            }
            else
            {
                moneyTradeOriginalFocusCallback = NavigationFocusField?.GetValue(moneyTradeNavigation);
                moneyTradeOriginalUnfocusCallback = NavigationUnfocusField?.GetValue(moneyTradeNavigation);
                moneyTradeOriginalPressedCallback = NavigationPressedField?.GetValue(moneyTradeNavigation);
            }
            moneyTradeNavigation.SetCallbacks(
                delegate { SetMoneyHovered(true); },
                delegate { SetMoneyHovered(false); },
                OpenMoneyOfferDialog);

            CoopMod.Logger.LogInfo(
                $"[Trade] Money interaction enabled (widgetDepth={moneyTradeLabel.raycastDepth}, " +
                $"size={width}x{height}, reusedCollider={!ownsMoneyTradeCollider})");
        }

        private void OnMoneyTradeHovered(GameObject go, bool isOver)
        {
            SetMoneyHovered(isOver);
        }

        private void OnMoneyTradeClicked(GameObject go)
        {
            CoopMod.Logger.LogInfo("[Trade] Money balance selected");
            OpenMoneyOfferDialog();
        }

        private void SetMoneyHovered(bool hovered)
        {
            if (moneyTradeLabel == null)
                return;
            moneyTradeLabel.color = hovered
                ? new Color(1f, 0.82f, 0.3f, moneyLabelNormalColor.a)
                : moneyLabelNormalColor;
            moneyTradeLabel.MarkAsChanged();
            if (hovered)
                Sounds.OnGUIHover(Sounds.ElementType.ItemCell);
        }

        private void RemoveMoneyInteraction()
        {
            if (moneyTradeLabel != null)
            {
                moneyTradeLabel.color = moneyLabelNormalColor;
                moneyTradeLabel.MarkAsChanged();
            }

            if (moneyTradeListener != null)
            {
                moneyTradeListener.onHover -= OnMoneyTradeHovered;
                moneyTradeListener.onClick -= OnMoneyTradeClicked;
                if (ownsMoneyTradeListener)
                    Destroy(moneyTradeListener);
            }

            if (moneyTradeNavigation != null)
            {
                if (ownsMoneyTradeNavigation)
                {
                    Destroy(moneyTradeNavigation);
                }
                else
                {
                    NavigationFocusField?.SetValue(moneyTradeNavigation, moneyTradeOriginalFocusCallback);
                    NavigationUnfocusField?.SetValue(moneyTradeNavigation, moneyTradeOriginalUnfocusCallback);
                    NavigationPressedField?.SetValue(moneyTradeNavigation, moneyTradeOriginalPressedCallback);
                }
            }
            if (ownsMoneyTradeAutoScroll && moneyTradeAutoScroll != null)
                Destroy(moneyTradeAutoScroll);

            if (moneyTradeCollider != null)
            {
                if (ownsMoneyTradeCollider)
                {
                    Destroy(moneyTradeCollider);
                }
                else
                {
                    moneyTradeCollider.enabled = moneyTradeColliderWasEnabled;
                    moneyTradeCollider.isTrigger = moneyTradeColliderWasTrigger;
                    moneyTradeCollider.size = moneyTradeColliderOriginalSize;
                    moneyTradeCollider.offset = moneyTradeColliderOriginalOffset;
                }
            }

            moneyTradeHitbox = null;
            moneyTradeLabel = null;
            moneyTradeCollider = null;
            ownsMoneyTradeCollider = false;
            moneyTradeListener = null;
            ownsMoneyTradeListener = false;
            moneyTradeNavigation = null;
            ownsMoneyTradeNavigation = false;
            moneyTradeAutoScroll = null;
            ownsMoneyTradeAutoScroll = false;
            moneyTradeOriginalFocusCallback = null;
            moneyTradeOriginalUnfocusCallback = null;
            moneyTradeOriginalPressedCallback = null;
        }

        private void OpenMoneyOfferDialog()
        {
            Item player = MainGame.me?.player?.data;
            ItemCountGUI picker = GUIElements.me?.item_count;
            if (!tradeBegun || !moneyTradingEnabled || player == null ||
                picker == null || picker.is_shown)
            {
                return;
            }

            int availableBronze = MoneyToBronze(player.money);
            int maximumBronze = ClampMoneyBronze(
                (long)availableBronze + localMoneyOfferBronze);
            if (maximumBronze <= 0)
            {
                GUIElements.me?.dialog?.OpenOK("You have no money to offer.");
                return;
            }

            if (moneyTradeLabel != null)
            {
                moneyTradeLabel.color = moneyLabelNormalColor;
                moneyTradeLabel.MarkAsChanged();
            }
            tradeGui?.button_tips?.Deactivate<ButtonTipsStr>();
            picker.Open(
                "flesh",
                0,
                maximumBronze,
                SetLocalMoneyOffer,
                1,
                BronzeToMoney);

            BaseItemCellGUI itemGui = ItemCountItemGuiField?.GetValue(picker) as BaseItemCellGUI;
            if (itemGui != null)
            {
                itemGui.DrawIcon("icon_copper", true, true);
                itemGui.interaction_enabled = false;
                ConfigureMoneyPickerIcon(itemGui.container?.icon);
                if (itemGui.item_name != null)
                    itemGui.item_name.text = "Money";
            }
            picker.header_label.text = "Money offer";

            SmartSlider slider = ItemCountSliderField?.GetValue(picker) as SmartSlider;
            int initial = Mathf.Clamp(localMoneyOfferBronze, 0, maximumBronze);
            if (slider != null)
            {
                ConfigureMoneyPickerSlider(slider, maximumBronze);
                slider.Open(initial, 0, maximumBronze, DrawMoneyPickerValue, true, true, 1);
                slider.number_of_steps = 0;
                if (slider.min_counter != null)
                    slider.min_counter.text = "0";
                if (slider.max_counter != null)
                    slider.max_counter.text = "All";
            }
            DrawMoneyPickerValue(initial);

            picker.SetOnHide(delegate
            {
                RestoreMoneyPickerVisuals();
                if (tradeGui != null && tradeBegun && !closingTradeGui)
                {
                    if (BaseGUI.for_gamepad)
                        tradeGui.button_tips.Activate<ButtonTipsStr>();
                    RedrawTradeGui();
                    if (BaseGUI.for_gamepad && tradeGui.gamepad_controller != null)
                    {
                        tradeGui.gamepad_controller.ReinitItems(true);
                        tradeGui.gamepad_controller.FocusOnFirstActive(-1);
                    }
                    TooltipsManager.Redraw();
                }
            }, true);
        }

        private void DrawMoneyPickerValue(int amountBronze)
        {
            ItemCountGUI picker = GUIElements.me?.item_count;
            if (picker?.price != null)
            {
                picker.price.text =
                    "Offer\n" + Trading.FormatMoney(
                        BronzeToMoney(amountBronze),
                        true,
                        true);
            }
        }

        private void SetLocalMoneyOffer(int amountBronze)
        {
            Item player = MainGame.me?.player?.data;
            if (!tradeBegun || !moneyTradingEnabled || player == null ||
                !ValidateMoneyOffer(amountBronze))
            {
                return;
            }

            int availableBronze = ClampMoneyBronze(
                (long)MoneyToBronze(player.money) + localMoneyOfferBronze);
            amountBronze = Mathf.Clamp(amountBronze, 0, availableBronze);
            player.money = BronzeToMoney(availableBronze - amountBronze);
            localMoneyOfferBronze = amountBronze;
            Sounds.PlaySound("item_put", null, false, 0f);
            SendLocalOffer();
            RedrawTradeGui();
        }

        private void ConfigureMoneyPickerIcon(UI2DSprite icon)
        {
            moneyPickerIcon = icon;
            if (moneyPickerIcon == null)
                return;

            moneyPickerIconOriginalWidth = moneyPickerIcon.width;
            moneyPickerIconOriginalHeight = moneyPickerIcon.height;

            const int maxIconSize = 28;
            float width = moneyPickerIcon.sprite2D?.rect.width ?? maxIconSize;
            float height = moneyPickerIcon.sprite2D?.rect.height ?? maxIconSize;
            float scale = maxIconSize / Mathf.Max(1f, Mathf.Max(width, height));
            moneyPickerIcon.width = Mathf.Max(1, Mathf.RoundToInt(width * scale));
            moneyPickerIcon.height = Mathf.Max(1, Mathf.RoundToInt(height * scale));
            moneyPickerIcon.MarkAsChanged();
        }

        private void ConfigureMoneyPickerSlider(SmartSlider slider, int maximumBronze)
        {
            moneyPickerSlider = slider;
            moneyPickerSliderOriginalAutoSteps = slider.auto_steps;
            slider.auto_steps = false;

            moneyPickerMinCounter = slider.min_counter;
            moneyPickerMaxCounter = slider.max_counter;
            moneyPickerMinCounterOriginalText = moneyPickerMinCounter?.text;
            moneyPickerMaxCounterOriginalText = moneyPickerMaxCounter?.text;

            moneyPickerTrack = slider.GetComponentInChildren<UISlider>(true);
            moneyPickerInput = slider.GetComponentInChildren<UIInput>(true);
            if (moneyPickerInput != null)
            {
                moneyPickerInputOriginalCharacterLimit = moneyPickerInput.characterLimit;
                moneyPickerInputOriginalValidation = moneyPickerInput.validation;
                moneyPickerInput.characterLimit = Math.Max(
                    9,
                    maximumBronze.ToString().Length);
                moneyPickerInput.validation = UIInput.Validation.Integer;
            }
            else
            {
                CoopMod.Logger.LogWarning("[Trade] Money picker input field was not found");
            }

            if (moneyPickerTrack != null)
            {
                UIWidget background = ProgressBarBackgroundField?.GetValue(moneyPickerTrack) as UIWidget;
                UIWidget rootWidget = slider.GetComponent<UIWidget>();
                int width = Math.Max(120, background?.width ?? rootWidget?.width ?? 120);
                int height = Math.Max(28, (background?.height ?? rootWidget?.height ?? 8) + 16);

                moneyPickerExpandedCollider = slider.gameObject.AddComponent<BoxCollider2D>();
                moneyPickerExpandedCollider.isTrigger = true;
                moneyPickerExpandedCollider.size = new Vector2(width, height);
                if (background != null)
                {
                    Vector3 center = slider.transform.InverseTransformPoint(
                        background.transform.position);
                    moneyPickerExpandedCollider.offset = new Vector2(center.x, center.y);
                }

                CoopMod.Logger.LogInfo(
                    $"[Trade] Money slider configured (range=0..{maximumBronze}, " +
                    $"hitbox={width}x{height}, inputLimit={moneyPickerInput?.characterLimit ?? 0})");
            }
            else
            {
                CoopMod.Logger.LogWarning("[Trade] Money picker slider component was not found");
            }
        }

        private void RestoreMoneyPickerVisuals()
        {
            if (moneyPickerIcon != null)
            {
                moneyPickerIcon.width = moneyPickerIconOriginalWidth;
                moneyPickerIcon.height = moneyPickerIconOriginalHeight;
                moneyPickerIcon.MarkAsChanged();
            }
            if (moneyPickerSlider != null)
                moneyPickerSlider.auto_steps = moneyPickerSliderOriginalAutoSteps;
            if (moneyPickerInput != null)
            {
                moneyPickerInput.characterLimit = moneyPickerInputOriginalCharacterLimit;
                moneyPickerInput.validation = moneyPickerInputOriginalValidation;
            }
            if (moneyPickerExpandedCollider != null)
                Destroy(moneyPickerExpandedCollider);
            if (moneyPickerMinCounter != null)
                moneyPickerMinCounter.text = moneyPickerMinCounterOriginalText;
            if (moneyPickerMaxCounter != null)
                moneyPickerMaxCounter.text = moneyPickerMaxCounterOriginalText;

            moneyPickerIcon = null;
            moneyPickerSlider = null;
            moneyPickerTrack = null;
            moneyPickerInput = null;
            moneyPickerExpandedCollider = null;
            moneyPickerMinCounter = null;
            moneyPickerMaxCounter = null;
            moneyPickerMinCounterOriginalText = null;
            moneyPickerMaxCounterOriginalText = null;
        }

        private static string FormatTradeMoneyOffer(int moneyBronze)
        {
            return Trading.FormatMoney(
                BronzeToMoney(moneyBronze),
                true,
                true);
        }

        private static float SanitizeMoney(float value)
        {
            return float.IsNaN(value) || float.IsInfinity(value)
                ? 0f
                : value;
        }

        private static int MoneyToBronze(float value)
        {
            value = SanitizeMoney(value);
            if (value <= 0f)
                return 0;
            return ClampMoneyBronze((long)Math.Round(
                (double)value * 100.0,
                MidpointRounding.AwayFromZero));
        }

        private static float BronzeToMoney(int amountBronze)
        {
            return ClampMoneyBronze(amountBronze) / 100f;
        }

        private static int ClampMoneyBronze(long amountBronze)
        {
            if (amountBronze <= 0L)
                return 0;
            return amountBronze >= MaxMoneyOfferBronze
                ? MaxMoneyOfferBronze
                : (int)amountBronze;
        }

        private void ApplyTradePanelHeads()
        {
            if (tradeGui?.player_panel?.spr_head == null ||
                tradeGui.vendor_panel?.spr_head == null)
            {
                return;
            }

            Sprite defaultPlayerHead =
                EasySpritesCollection.GetSprite("000_hed_down", false, "");
            tradeGui.player_panel.spr_head.sprite2D = defaultPlayerHead;

            WorldGameObject remote =
                OnlineCoopManager.Instance?.GetRemotePlayer(localPartner);
            Sprite remoteHead = remote?.GetHeadSprite();

            // Network player proxies do not carry a SkinPreset id, so the
            // vanilla GetHeadSprite path returns null for them. Both players
            // use the keeper base head; synchronized cosmetics are material
            // tints and do not provide an alternate portrait sprite.
            tradeGui.vendor_panel.spr_head.sprite2D = remoteHead ?? defaultPlayerHead;
            FindTradeReadyFrames();
        }

        private void ConfigureTradeOfferMoneyLabel(UILabel label)
        {
            if (tradeOfferMoneyLabel == label)
                return;

            RestoreTradeOfferMoneyLabel();
            tradeOfferMoneyLabel = label;
            tradeOfferMoneyOriginalOverflow = label.overflowMethod;
            label.overflowMethod = UILabel.Overflow.ShrinkContent;
        }

        private void RestoreTradeOfferMoneyLabel()
        {
            if (tradeOfferMoneyLabel != null)
                tradeOfferMoneyLabel.overflowMethod = tradeOfferMoneyOriginalOverflow;
            tradeOfferMoneyLabel = null;
        }

        private void FindTradeReadyFrames()
        {
            if (tradeReadyFrameLookupAttempted)
                return;
            if (tradeGui?.player_panel?.spr_head == null ||
                tradeGui.vendor_panel?.spr_head == null)
            {
                return;
            }
            tradeReadyFrameLookupAttempted = true;

            localTradeReadyFrame = FindPortraitFrame(tradeGui, tradeGui.player_panel.spr_head);
            if (localTradeReadyFrame != null)
                localTradeReadyFrameOriginalColor = localTradeReadyFrame.color;

            remoteTradeReadyFrame = FindPortraitFrame(tradeGui, tradeGui.vendor_panel.spr_head);
            if (remoteTradeReadyFrame != null)
                remoteTradeReadyFrameOriginalColor = remoteTradeReadyFrame.color;
        }

        private static UIWidget FindPortraitFrame(VendorGUI gui, UI2DSprite head)
        {
            if (gui == null || head == null)
                return null;

            UIWidget exactMatch = null;
            string exactMatchName = string.Empty;
            float exactMatchScore = float.MaxValue;
            UIWidget fallback = null;
            string fallbackName = string.Empty;
            float fallbackScore = float.MaxValue;
            Vector3 headPosition = gui.transform.InverseTransformPoint(head.transform.position);
            UIWidget[] widgets = gui.GetComponentsInChildren<UIWidget>(true);
            for (int i = 0; i < widgets.Length; i++)
            {
                UIWidget candidate = widgets[i];
                if (candidate == null || candidate == head)
                {
                    continue;
                }

                Vector3 position = gui.transform.InverseTransformPoint(candidate.transform.position);
                float dx = Mathf.Abs(position.x - headPosition.x);
                float dy = Mathf.Abs(position.y - headPosition.y);
                string spriteName = GetWidgetSpriteName(candidate);
                string objectName = candidate.gameObject.name ?? string.Empty;

                // The silver semicircle around both vendor portraits is the
                // hud_head_back widget. It is not parented below InventoryPanelGUI
                // in every prefab variant, and its dimensions/pivot differ from the
                // face sprite. Match its identity before applying geometry filters.
                if (IsPortraitSurroundName(spriteName) ||
                    IsPortraitSurroundName(objectName))
                {
                    float identityScore = dx * dx + dy * dy;
                    if (identityScore < exactMatchScore)
                    {
                        exactMatch = candidate;
                        exactMatchName = spriteName;
                        exactMatchScore = identityScore;
                    }
                    continue;
                }

                if (!(candidate is UI2DSprite) && !(candidate is UISprite) ||
                    candidate.width < 25 || candidate.width > 180 ||
                    candidate.height < 25 || candidate.height > 180 ||
                    dx > 64f || dy > 64f)
                {
                    continue;
                }

                float sizePenalty = Mathf.Abs(candidate.width - 47) * 0.1f +
                                    Mathf.Abs(candidate.height - 51) * 0.1f;
                float score = dx * dx + dy * dy + sizePenalty;
                if (score >= fallbackScore)
                    continue;

                fallback = candidate;
                fallbackName = spriteName;
                fallbackScore = score;
            }

            UIWidget result = exactMatch ?? fallback;
            string resultName = exactMatch != null ? exactMatchName : fallbackName;
            if (result == null)
            {
                CoopMod.Logger.LogWarning(
                    $"[Trade] Could not identify the portrait surround for ready lighting " +
                    $"(head='{head.gameObject.name}', size={head.width}x{head.height})");
                return null;
            }

            CoopMod.Logger.LogInfo(
                $"[Trade] Ready light uses '{resultName}' on '{result.gameObject.name}' " +
                $"({result.width}x{result.height}, exact={exactMatch != null})");
            return result;
        }

        private static bool IsPortraitSurroundName(string name)
        {
            return !string.IsNullOrEmpty(name) &&
                   (string.Equals(name, "hud_head_back", StringComparison.OrdinalIgnoreCase) ||
                    name.IndexOf("hud_head_back", StringComparison.OrdinalIgnoreCase) >= 0);
        }

        private static string GetWidgetSpriteName(UIWidget widget)
        {
            if (widget is UI2DSprite sprite2D)
                return sprite2D.sprite2D?.name ?? sprite2D.gameObject.name;
            if (widget is UISprite atlasSprite)
                return !string.IsNullOrEmpty(atlasSprite.spriteName)
                    ? atlasSprite.spriteName
                    : atlasSprite.gameObject.name;
            return widget?.gameObject?.name ?? string.Empty;
        }

        private void UpdateTradeReadyIndicators()
        {
            FindTradeReadyFrames();
            SetPortraitReadyLight(
                localTradeReadyFrame,
                localTradeReadyFrameOriginalColor,
                localReady);
            SetPortraitReadyLight(
                remoteTradeReadyFrame,
                remoteTradeReadyFrameOriginalColor,
                remoteReady);
        }

        private static void SetPortraitReadyLight(
            UIWidget frame,
            Color originalColor,
            bool ready)
        {
            if (frame == null)
                return;

            frame.color = ready
                ? new Color(1f, 0.72f, 0.22f, originalColor.a)
                : originalColor;
            frame.MarkAsChanged();
        }

        private void RemoveTradeReadyIndicators()
        {
            SetPortraitReadyLight(
                localTradeReadyFrame,
                localTradeReadyFrameOriginalColor,
                false);
            SetPortraitReadyLight(
                remoteTradeReadyFrame,
                remoteTradeReadyFrameOriginalColor,
                false);
            localTradeReadyFrame = null;
            remoteTradeReadyFrame = null;
            tradeReadyFrameLookupAttempted = false;
        }

        private static List<string> SerializeOffer(Item root)
        {
            var result = new List<string>();
            if (root?.inventory == null)
                return result;
            for (int i = 0; i < root.inventory.Count && i < OfferSlots; i++)
                result.Add(root.inventory[i].ToJSON());
            return result;
        }

        private static void ReplaceOfferRoot(Item root, List<string> offer)
        {
            if (root == null)
                return;
            root.inventory.Clear();
            for (int i = 0; i < offer.Count; i++)
            {
                Item item = DeserializeItem(offer[i]);
                if (item != null)
                    root.inventory.Add(item);
            }
        }

        private static Item DeserializeItem(string json)
        {
            try
            {
                Item item = JsonUtility.FromJson<Item>(json);
                if (item == null || item.IsEmpty())
                    return null;
                EnsureItemLists(item);
                item.equipped_as = ItemDefinition.EquipmentType.None;
                return item;
            }
            catch
            {
                return null;
            }
        }

        private static void EnsureItemLists(Item item)
        {
            if (item.inventory == null)
                item.inventory = new List<Item>();
            if (item.secondary_inventory == null)
                item.secondary_inventory = new List<Item>();
            for (int i = 0; i < item.inventory.Count; i++)
                EnsureItemLists(item.inventory[i]);
            for (int i = 0; i < item.secondary_inventory.Count; i++)
                EnsureItemLists(item.secondary_inventory[i]);
        }

        private static bool ValidateOffer(List<string> offer)
        {
            if (offer == null || offer.Count > OfferSlots)
                return false;
            int total = 0;
            for (int i = 0; i < offer.Count; i++)
            {
                string json = offer[i] ?? string.Empty;
                total += json.Length;
                if (json.Length == 0 || json.Length > MaxItemJsonLength ||
                    total > MaxOfferJsonLength || DeserializeItem(json) == null)
                {
                    return false;
                }
            }
            return true;
        }

        private static bool ValidateMoneyOffer(int amountBronze)
        {
            return amountBronze >= 0 && amountBronze <= MaxMoneyOfferBronze;
        }

        private static bool ValidateInventorySnapshot(string inventoryJson)
        {
            return !string.IsNullOrEmpty(inventoryJson) &&
                   inventoryJson.Length <= MaxInventoryJsonLength &&
                   DeserializeItem(inventoryJson) != null;
        }

        private static string ReadInventorySnapshot(ref MsgReader reader)
        {
            string inventoryJson = reader.ReadString();
            if (!ValidateInventorySnapshot(inventoryJson))
                throw new InvalidOperationException("Trade inventory snapshot is invalid.");
            return inventoryJson;
        }

        private static List<string> ReadOffer(ref MsgReader reader)
        {
            int count = reader.ReadByte();
            if (count > OfferSlots)
                throw new InvalidOperationException("Trade offer has too many slots.");
            var offer = new List<string>(count);
            int total = 0;
            for (int i = 0; i < count; i++)
            {
                string json = reader.ReadString();
                total += json.Length;
                if (json.Length > MaxItemJsonLength || total > MaxOfferJsonLength)
                    throw new InvalidOperationException("Trade offer payload is too large.");
                offer.Add(json);
            }
            return offer;
        }

        private static int ReadMoneyOffer(ref MsgReader reader)
        {
            if (reader.Remaining == 0)
                return 0;
            if (reader.Remaining < sizeof(int))
                throw new InvalidOperationException("Trade money offer is truncated.");
            return reader.ReadInt32();
        }

        private static byte ReadProtocolVersion(ref MsgReader reader)
        {
            return reader.Remaining > 0 ? reader.ReadByte() : (byte)1;
        }

        private static void WriteOffer(MsgWriter writer, List<string> offer)
        {
            writer.Write((byte)offer.Count);
            for (int i = 0; i < offer.Count; i++)
                writer.Write(offer[i]);
        }

        private void SendRequest(CSteamID target, ulong sessionId)
        {
            using (var writer = new MsgWriter(Op.PlayerTrade, 32))
            {
                writer.Write((byte)TradePhase.Request);
                writer.Write(target.m_SteamID);
                writer.Write(sessionId);
                writer.Write(TradeProtocolVersion);
                SteamP2PManager.Instance.SendToHost(writer.ToArray());
            }
        }

        private void SendParticipantPhase(TradePhase phase, ulong sessionId)
        {
            if (IsHost)
            {
                if (phase == TradePhase.Accept)
                {
                    HostHandleAccept(
                        SteamUser.GetSteamID(),
                        sessionId,
                        TradeProtocolVersion);
                }
                else if (phase == TradePhase.Decline)
                    HostHandleDecline(SteamUser.GetSteamID(), sessionId);
                return;
            }

            using (var writer = new MsgWriter(Op.PlayerTrade, 16))
            {
                writer.Write((byte)phase);
                writer.Write(sessionId);
                if (phase == TradePhase.Accept)
                    writer.Write(TradeProtocolVersion);
                SteamP2PManager.Instance.SendToHost(writer.ToArray());
            }
        }

        private void SendOffer(
            ulong sessionId,
            int revision,
            string inventoryJson,
            List<string> offer,
            int moneyOfferBronze)
        {
            using (var writer = new MsgWriter(Op.PlayerTrade, 512))
            {
                writer.Write((byte)TradePhase.Offer);
                writer.Write(sessionId);
                writer.Write(revision);
                writer.Write(inventoryJson);
                WriteOffer(writer, offer);
                writer.Write(moneyOfferBronze);
                SteamP2PManager.Instance.SendToHost(writer.ToArray());
            }
        }

        private void SendReady(ulong sessionId, int revision, bool ready)
        {
            if (IsHost)
            {
                HostHandleReady(SteamUser.GetSteamID(), sessionId, revision, ready);
                return;
            }

            using (var writer = new MsgWriter(Op.PlayerTrade, 20))
            {
                writer.Write((byte)TradePhase.Ready);
                writer.Write(sessionId);
                writer.Write(revision);
                writer.Write(ready);
                SteamP2PManager.Instance.SendToHost(writer.ToArray());
            }
        }

        private void SendCancel(ulong sessionId, string reason)
        {
            if (IsHost)
            {
                HostHandleCancel(SteamUser.GetSteamID(), sessionId, reason);
                return;
            }

            using (var writer = new MsgWriter(Op.PlayerTrade, 96))
            {
                writer.Write((byte)TradePhase.Cancel);
                writer.Write(sessionId);
                writer.Write(reason ?? string.Empty);
                SteamP2PManager.Instance.SendToHost(writer.ToArray());
            }
        }

        private void SendCommitAck(ulong sessionId)
        {
            if (IsHost)
            {
                HostHandleCommitAck(SteamUser.GetSteamID(), sessionId);
                return;
            }

            using (var writer = new MsgWriter(Op.PlayerTrade, 16))
            {
                writer.Write((byte)TradePhase.CommitAck);
                writer.Write(sessionId);
                SteamP2PManager.Instance.SendToHost(writer.ToArray());
            }
        }

        private void DeliverInvite(CSteamID target, ulong sessionId, CSteamID inviter)
        {
            if (target == SteamUser.GetSteamID())
            {
                ReceiveInvite(sessionId, inviter);
                return;
            }
            SendToPeer(target, TradePhase.Invite, sessionId, inviter.m_SteamID);
        }

        private void DeliverBegin(
            CSteamID target,
            ulong sessionId,
            CSteamID partner,
            bool enableMoneyTrading)
        {
            if (target == SteamUser.GetSteamID())
            {
                ReceiveBegin(sessionId, partner, enableMoneyTrading);
                return;
            }
            using (var writer = new MsgWriter(Op.PlayerTrade, 24))
            {
                writer.Write((byte)TradePhase.Begin);
                writer.Write(sessionId);
                writer.Write(partner.m_SteamID);
                writer.Write(enableMoneyTrading);
                SteamP2PManager.Instance.SendBinary(target, writer.ToArray());
            }
        }

        private void DeliverOffer(
            CSteamID target,
            ulong sessionId,
            int revision,
            string inventoryJson,
            List<string> offer,
            int moneyOfferBronze)
        {
            if (target == SteamUser.GetSteamID())
            {
                ReceiveRemoteOffer(
                    sessionId,
                    revision,
                    inventoryJson,
                    offer,
                    moneyOfferBronze);
                return;
            }
            using (var writer = new MsgWriter(Op.PlayerTrade, 512))
            {
                writer.Write((byte)TradePhase.Offer);
                writer.Write(sessionId);
                writer.Write(revision);
                writer.Write(inventoryJson);
                WriteOffer(writer, offer);
                writer.Write(moneyOfferBronze);
                SteamP2PManager.Instance.SendBinary(target, writer.ToArray());
            }
        }

        private void DeliverReady(CSteamID target, ulong sessionId, bool ready)
        {
            if (target == SteamUser.GetSteamID())
            {
                ReceiveRemoteReady(sessionId, ready);
                return;
            }
            using (var writer = new MsgWriter(Op.PlayerTrade, 16))
            {
                writer.Write((byte)TradePhase.Ready);
                writer.Write(sessionId);
                writer.Write(ready);
                SteamP2PManager.Instance.SendBinary(target, writer.ToArray());
            }
        }

        private void DeliverCancel(CSteamID target, ulong sessionId, string reason)
        {
            if (target == SteamUser.GetSteamID())
            {
                ReceiveCancel(sessionId, reason);
                return;
            }
            using (var writer = new MsgWriter(Op.PlayerTrade, 96))
            {
                writer.Write((byte)TradePhase.Cancel);
                writer.Write(sessionId);
                writer.Write(reason ?? string.Empty);
                SteamP2PManager.Instance.SendBinary(target, writer.ToArray());
            }
        }

        private void DeliverCommit(
            CSteamID target,
            ulong sessionId,
            int expectedRevision,
            List<string> incoming,
            int incomingMoneyBronze)
        {
            if (target == SteamUser.GetSteamID())
            {
                ReceiveCommit(
                    sessionId,
                    expectedRevision,
                    incoming,
                    incomingMoneyBronze);
                return;
            }
            using (var writer = new MsgWriter(Op.PlayerTrade, 512))
            {
                writer.Write((byte)TradePhase.Commit);
                writer.Write(sessionId);
                writer.Write(expectedRevision);
                WriteOffer(writer, incoming);
                writer.Write(incomingMoneyBronze);
                SteamP2PManager.Instance.SendBinary(target, writer.ToArray());
            }
        }

        private static void SendToPeer(
            CSteamID target,
            TradePhase phase,
            ulong sessionId,
            ulong peerId)
        {
            using (var writer = new MsgWriter(Op.PlayerTrade, 24))
            {
                writer.Write((byte)phase);
                writer.Write(sessionId);
                writer.Write(peerId);
                SteamP2PManager.Instance.SendBinary(target, writer.ToArray());
            }
        }

        private static ulong CreateSessionId()
        {
            ulong id = (ulong)DateTime.UtcNow.Ticks ^ SteamUser.GetSteamID().m_SteamID;
            return id == 0 ? 1UL : id;
        }

        private static bool IsCurrentLobbyMember(CSteamID player)
        {
            CSteamID lobby = SteamLobbyManager.Instance?.CurrentLobbyID ?? CSteamID.Nil;
            if (lobby == CSteamID.Nil || player == CSteamID.Nil)
                return false;
            int count = SteamMatchmaking.GetNumLobbyMembers(lobby);
            for (int i = 0; i < count; i++)
            {
                if (SteamMatchmaking.GetLobbyMemberByIndex(lobby, i) == player)
                    return true;
            }
            return false;
        }

        private static bool ArePlayersNear(CSteamID a, CSteamID b, float maxDistance)
        {
            if (!TryGetPlayerPosition(a, out Vector3 aPos) ||
                !TryGetPlayerPosition(b, out Vector3 bPos))
            {
                return false;
            }
            Vector3 delta = aPos - bPos;
            return delta.x * delta.x + delta.y * delta.y <= maxDistance * maxDistance;
        }

        private static bool TryGetPlayerPosition(CSteamID player, out Vector3 position)
        {
            position = Vector3.zero;
            if (player == SteamUser.GetSteamID())
            {
                if (MainGame.me?.player == null)
                    return false;
                position = MainGame.me.player.transform.position;
                return true;
            }
            WorldGameObject remote = OnlineCoopManager.Instance?.GetRemotePlayer(player);
            if (remote == null)
                return false;
            position = remote.transform.position;
            return true;
        }

        private static string GetName(CSteamID player)
        {
            if (player == CSteamID.Nil)
                return "Player";
            string name = player == SteamUser.GetSteamID()
                ? SteamFriends.GetPersonaName()
                : SteamFriends.GetFriendPersonaName(player);
            return string.IsNullOrEmpty(name) ? "Player" : name;
        }

        private static void SetButtonLabel(UIButton button, string text)
        {
            if (button == null)
                return;
            UILabel[] labels = button.gameObject.GetComponentsInChildren<UILabel>(true);
            for (int i = 0; i < labels.Length; i++)
                labels[i].text = text;
        }

        private static void SetButtonEnabled(UIButton button, bool enabled)
        {
            if (button == null)
                return;
            UIButton[] buttons = button.gameObject.GetComponentsInChildren<UIButton>(true);
            for (int i = 0; i < buttons.Length; i++)
            {
                buttons[i].isEnabled = enabled;
                buttons[i].SetState(
                    enabled ? UIButtonColor.State.Normal : UIButtonColor.State.Disabled,
                    true);
            }
        }
    }
}

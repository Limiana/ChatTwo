﻿using System.Runtime.InteropServices;
using System.Text;
using ChatTwo.Code;
using ChatTwo.Util;
using Dalamud.Game.Text.SeStringHandling;
using Dalamud.Hooking;
using Dalamud.Logging;
using Dalamud.Memory;
using FFXIVClientStructs.FFXIV.Client.System.Framework;
using FFXIVClientStructs.FFXIV.Client.System.String;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Client.UI.Misc;
using FFXIVClientStructs.FFXIV.Client.UI.Shell;
using FFXIVClientStructs.FFXIV.Component.GUI;

namespace ChatTwo;

internal unsafe class GameFunctions : IDisposable {
    private static class Signatures {
        internal const string ChatLogRefresh = "40 53 56 57 48 81 EC ?? ?? ?? ?? 48 8B 05 ?? ?? ?? ?? 48 33 C4 48 89 84 24 ?? ?? ?? ?? 49 8B F0 8B FA";
        internal const string ChangeChannelName = "E8 ?? ?? ?? ?? BA ?? ?? ?? ?? 48 8D 4D B0 48 8B F8 E8 ?? ?? ?? ?? 41 8B D6";

        internal const string CurrentChatEntryOffset = "8B 77 ?? 8D 46 01 89 47 14 81 FE ?? ?? ?? ?? 72 03 FF 47";
        internal const string AgentContextYesNo = "E8 ?? ?? ?? ?? E9 ?? ?? ?? ?? 48 8B CB E8 ?? ?? ?? ?? 84 C0 74 3A";
    }

    private delegate byte ChatLogRefreshDelegate(IntPtr log, ushort eventId, AtkValue* value);

    private delegate IntPtr ChangeChannelNameDelegate(IntPtr agent);

    private delegate IntPtr AgentContextYesNoDelegate(AgentInterface* context, uint a2, byte* playerName, ushort playerWorld, uint a5, byte a6);

    internal delegate void ChatActivatedEventDelegate(string? input);

    #region Functions

    [Signature("E8 ?? ?? ?? ?? 0F B7 44 37 ??")]
    private readonly delegate* unmanaged<RaptureShellModule*, int, uint, Utf8String*, byte, void> _changeChatChannel;

    [Signature("4C 8B 81 ?? ?? ?? ?? 4D 85 C0 74 17")]
    private readonly delegate* unmanaged<RaptureLogModule*, uint, ulong> _getContentIdForChatEntry;

    [Signature("E8 ?? ?? ?? ?? 8B FD 8B CD")]
    private readonly delegate* unmanaged<IntPtr, uint, IntPtr> _indexer;

    [Signature("E8 ?? ?? ?? ?? 33 C0 EB 51")]
    private readonly delegate* unmanaged<IntPtr, ulong, byte*, ushort, void> _inviteToParty;

    [Signature(("E8 ?? ?? ?? ?? E9 ?? ?? ?? ?? 48 8B CB E8 ?? ?? ?? ?? 45 33 C9"))]
    private readonly delegate* unmanaged<IntPtr, ulong, ushort, byte*, byte> _inviteToNoviceNetwork;

    [Signature("40 53 48 83 EC 20 48 8B D9 48 8B 49 10 48 8B 01 FF 90 ?? ?? ?? ?? 48 8B 48 48")]
    private readonly delegate* unmanaged<AgentInterface*, byte> _friendRequestBool;

    [Signature("E8 ?? ?? ?? ?? EB 35 BA")]
    private readonly delegate* unmanaged<uint, uint, ulong, uint, byte, byte> _tryOn;

    [Signature("E8 ?? ?? ?? ?? EB 7B 49 8B 06")]
    private readonly delegate* unmanaged<AgentInterface*, uint, void> _linkItem;

    [Signature("E8 ?? ?? ?? ?? EB 3F 83 F8 FE")]
    private readonly delegate* unmanaged<AgentInterface*, ushort, uint, byte, void> _itemComparison;

    [Signature("E8 ?? ?? ?? ?? E9 ?? ?? ?? ?? 41 B4 01")]
    private readonly delegate* unmanaged<IntPtr, uint, void> _searchForRecipesUsingItem;

    [Signature("E8 ?? ?? ?? ?? EB 45 45 33 C9")]
    private readonly delegate* unmanaged<void*, uint, byte, void> _searchForItem;

    #endregion

    internal const int HqItemOffset = 1_000_000;

    private Plugin Plugin { get; }
    private Hook<ChatLogRefreshDelegate>? ChatLogRefreshHook { get; }
    private Hook<ChangeChannelNameDelegate>? ChangeChannelNameHook { get; }

    private readonly int? _currentChatEntryOffset;
    private readonly AgentContextYesNoDelegate? _agentContextYesNo;

    internal event ChatActivatedEventDelegate? ChatActivated;

    internal (InputChannel channel, List<Chunk> name) ChatChannel { get; private set; }

    internal GameFunctions(Plugin plugin) {
        this.Plugin = plugin;

        this.Plugin.SigScanner.ScanFunctions(this);

        if (this.Plugin.SigScanner.TryScanText(Signatures.ChatLogRefresh, out var chatLogPtr)) {
            this.ChatLogRefreshHook = new Hook<ChatLogRefreshDelegate>(chatLogPtr, this.ChatLogRefreshDetour);
            this.ChatLogRefreshHook.Enable();
        }

        if (this.Plugin.SigScanner.TryScanText(Signatures.ChangeChannelName, out var channelNamePtr)) {
            this.ChangeChannelNameHook = new Hook<ChangeChannelNameDelegate>(channelNamePtr, this.ChangeChannelNameDetour);
            this.ChangeChannelNameHook.Enable();
        }

        if (this.Plugin.SigScanner.TryScanText(Signatures.CurrentChatEntryOffset, out var entryOffsetPtr)) {
            this._currentChatEntryOffset = *(byte*) (entryOffsetPtr + 2);
        }

        if (this.Plugin.SigScanner.TryScanText(Signatures.AgentContextYesNo, out var sendFriendRequestPtr)) {
            this._agentContextYesNo = Marshal.GetDelegateForFunctionPointer<AgentContextYesNoDelegate>(sendFriendRequestPtr);
        }

        this.Plugin.ClientState.Login += this.Login;
        this.Login(null, null);
    }

    public void Dispose() {
        this.Plugin.ClientState.Login -= this.Login;
        this.ChangeChannelNameHook?.Dispose();
        this.ChatLogRefreshHook?.Dispose();
        this.ChatActivated = null;
    }

    private void Login(object? sender, EventArgs? e) {
        if (this.ChangeChannelNameHook == null) {
            return;
        }

        var agent = Framework.Instance()->GetUiModule()->GetAgentModule()->GetAgentByInternalId(AgentId.ChatLog);
        if (agent == null) {
            return;
        }

        this.ChangeChannelNameDetour((IntPtr) agent);
    }

    internal uint? GetCurrentChatLogEntryIndex() {
        if (this._currentChatEntryOffset == null) {
            return null;
        }

        var log = (IntPtr) Framework.Instance()->GetUiModule()->GetRaptureLogModule();
        return *(uint*) (log + this._currentChatEntryOffset.Value);
    }

    internal ulong? GetContentIdForChatLogEntry(uint index) {
        if (this._getContentIdForChatEntry == null) {
            return null;
        }

        return this._getContentIdForChatEntry(Framework.Instance()->GetUiModule()->GetRaptureLogModule(), index);
    }

    internal void InviteToParty(string name, ushort world) {
        if (this._inviteToParty == null || this._indexer == null) {
            return;
        }

        var uiModule = Framework.Instance()->GetUiModule();
        // 6.05: 20D722
        var func = (delegate*<UIModule*, IntPtr>) uiModule->vfunc[33];
        var toIndex = func(uiModule);
        var a1 = this._indexer(toIndex, 1);

        fixed (byte* namePtr = name.ToTerminatedBytes()) {
            // can specify content id if we have it, but there's no need
            this._inviteToParty(a1, 0, namePtr, world);
        }
    }

    internal void SendFriendRequest(string name, ushort world) {
        if (this._agentContextYesNo == null || this._friendRequestBool == null) {
            return;
        }

        var agent = Framework.Instance()->GetUiModule()->GetAgentModule()->GetAgentByInternalId(AgentId.Context);
        var a6 = this._friendRequestBool(agent);

        fixed (byte* namePtr = name.ToTerminatedBytes()) {
            // 6.05: 20DA57
            this._agentContextYesNo(agent, 149, namePtr, world, 10_955, a6);
        }
    }

    internal void InviteToNoviceNetwork(string name, ushort world) {
        if (this._inviteToNoviceNetwork == null || this._indexer == null) {
            return;
        }

        var uiModule = Framework.Instance()->GetUiModule();
        // 6.05: 20D722
        var func = (delegate*<UIModule*, IntPtr>) uiModule->vfunc[33];
        var toIndex = func(uiModule);
        // 6.05: 20E4CB
        var a1 = this._indexer(toIndex, 0x11);

        fixed (byte* namePtr = name.ToTerminatedBytes()) {
            // can specify content id if we have it, but there's no need
            this._inviteToNoviceNetwork(a1, 0, world, namePtr);
        }
    }

    internal void SetChatChannel(InputChannel channel, string? tellTarget = null) {
        if (this._changeChatChannel == null) {
            return;
        }

        var bytes = Encoding.UTF8.GetBytes(tellTarget ?? "");
        var target = new Utf8String();
        fixed (byte* tellTargetPtr = bytes) {
            var zero = stackalloc byte[1];
            zero[0] = 0;

            target.StringPtr = tellTargetPtr == null ? zero : tellTargetPtr;
            target.StringLength = bytes.Length;
            this._changeChatChannel(RaptureShellModule.Instance, (int) (channel + 1), channel.LinkshellIndex(), &target, 1);
        }
    }

    internal static void SetAddonInteractable(string name, bool interactable) {
        var unitManager = AtkStage.GetSingleton()->RaptureAtkUnitManager;

        var addon = (IntPtr) unitManager->GetAddonByName(name);
        if (addon == IntPtr.Zero) {
            return;
        }

        var flags = (uint*) (addon + 0x180);
        if (interactable) {
            *flags &= ~(1u << 22);
        } else {
            *flags |= 1 << 22;
        }
    }

    internal void SetChatInteractable(bool interactable) {
        for (var i = 0; i < 4; i++) {
            SetAddonInteractable($"ChatLogPanel_{i}", interactable);
        }

        SetAddonInteractable("ChatLog", interactable);
    }

    internal static bool IsAddonInteractable(string name) {
        var unitManager = AtkStage.GetSingleton()->RaptureAtkUnitManager;

        var addon = (IntPtr) unitManager->GetAddonByName(name);
        if (addon == IntPtr.Zero) {
            return false;
        }

        var flags = (uint*) (addon + 0x180);
        return (*flags & (1 << 22)) == 0;
    }

    internal void OpenItemTooltip(uint id) {
        var atkStage = AtkStage.GetSingleton();
        var agent = Framework.Instance()->GetUiModule()->GetAgentModule()->GetAgentByInternalId(AgentId.ItemDetail);
        var addon = atkStage->RaptureAtkUnitManager->GetAddonByName("ItemDetail");
        if (agent == null || addon == null) {
            // atkStage ain't gonna be null or we have bigger problems
            return;
        }

        var agentPtr = (IntPtr) agent;

        // addresses mentioned here are 6.01
        // see the call near the end of AgentItemDetail.Update
        // offsets valid as of 6.01

        // 8BFC49: sets some shit
        *(uint*) (agentPtr + 0x20) = 22;
        // 8C04C8: switch goes down to default, which is what we want
        *(byte*) (agentPtr + 0x118) = 1;
        // 8BFCF6: item id when hovering over item in chat
        *(uint*) (agentPtr + 0x11C) = id;
        // 8BFCE4: always 0 when hovering over item in chat
        *(uint*) (agentPtr + 0x120) = 0;
        // 8C0B55: skips a check to do with inventory
        *(byte*) (agentPtr + 0x128) &= 0xEF;
        // 8BFC7C: when set to 1, lets everything continue (one frame)
        *(byte*) (agentPtr + 0x146) = 1;
        // 8BFC89: skips early return
        *(byte*) (agentPtr + 0x14A) = 0;

        // this just probably needs to be set
        agent->AddonId = (uint) addon->ID;

        // vcall from E8 ?? ?? ?? ?? 0F B7 C0 48 83 C4 60
        var vf5 = (delegate*<AtkUnitBase*, byte, uint, void>*) ((IntPtr) addon->VTable + 40);
        // E8872D: lets vf5 actually run
        *(byte*) ((IntPtr) atkStage + 0x2B4) |= 2;
        (*vf5)(addon, 0, 15);
    }

    internal void CloseItemTooltip() {
        // hide addon first to prevent the "addon close" sound
        var addon = AtkStage.GetSingleton()->RaptureAtkUnitManager->GetAddonByName("ItemDetail");
        if (addon != null) {
            addon->Hide(true);
        }

        var agent = Framework.Instance()->GetUiModule()->GetAgentModule()->GetAgentByInternalId(AgentId.ItemDetail);
        if (agent != null) {
            agent->Hide();
        }
    }

    private byte ChatLogRefreshDetour(IntPtr log, ushort eventId, AtkValue* value) {
        if (eventId == 0x31 && value != null && value->UInt is 0x05 or 0x0C) {
            string? eventInput = null;

            var str = value + 2;
            if (str != null && str->String != null) {
                var input = MemoryHelper.ReadStringNullTerminated((IntPtr) str->String);
                if (input.Length > 0) {
                    eventInput = input;
                }
            }

            try {
                this.ChatActivated?.Invoke(eventInput);
            } catch (Exception ex) {
                PluginLog.LogError(ex, "Error in ChatActivated event");
            }

            return 0;
        }

        return this.ChatLogRefreshHook!.Original(log, eventId, value);
    }

    private IntPtr ChangeChannelNameDetour(IntPtr agent) {
        // Last ShB patch
        // +0x40 = chat channel (byte or uint?)
        //         channel is 17 (maybe 18?) for tells
        // +0x48 = pointer to channel name string
        var ret = this.ChangeChannelNameHook!.Original(agent);
        if (agent == IntPtr.Zero) {
            return ret;
        }

        // E8 ?? ?? ?? ?? 8D 48 F7
        // RaptureShellModule + 0xFD0
        var shellModule = (IntPtr) Framework.Instance()->GetUiModule()->GetRaptureShellModule();
        if (shellModule == IntPtr.Zero) {
            return ret;
        }

        var channel = *(uint*) (shellModule + 0xFD0);

        // var channel = *(uint*) (agent + 0x40);
        if (channel is 17 or 18) {
            channel = 0;
        }

        SeString? name = null;
        var namePtrPtr = (byte**) (agent + 0x48);
        if (namePtrPtr != null) {
            var namePtr = *namePtrPtr;
            name = MemoryHelper.ReadSeStringNullTerminated((IntPtr) namePtr);
            if (name.Payloads.Count == 0) {
                name = null;
            }
        }

        if (name == null) {
            return ret;
        }

        var nameChunks = ChunkUtil.ToChunks(name, null).ToList();
        if (nameChunks.Count > 0 && nameChunks[0] is TextChunk text) {
            text.Content = text.Content.TrimStart('\uE01E').TrimStart();
        }

        this.ChatChannel = ((InputChannel) channel, nameChunks);

        return ret;
    }

    // These context menu things come from AgentChatLog.vf0 at the bottom
    // 0x10000: item comparison
    // 0x10001: try on
    // 0x10002: search for item
    // 0x10003: link
    // 0x10005: copy item name
    // 0x10006: search recipes using this material


    internal void TryOn(uint itemId, byte stainId) {
        if (this._tryOn == null) {
            return;
        }

        this._tryOn(0xFF, itemId, stainId, 0, 0);
    }

    internal void LinkItem(uint itemId) {
        if (this._linkItem == null) {
            return;
        }

        var agent = Framework.Instance()->GetUiModule()->GetAgentModule()->GetAgentByInternalId(AgentId.ChatLog);
        this._linkItem(agent, itemId);
    }

    internal void OpenItemComparison(uint itemId) {
        if (this._itemComparison == null) {
            return;
        }

        var agent = Framework.Instance()->GetUiModule()->GetAgentModule()->GetAgentByInternalId(AgentId.ItemCompare);
        this._itemComparison(agent, 0x4D, itemId, 0);
    }

    internal void SearchForRecipesUsingItem(uint itemId) {
        if (this._searchForRecipesUsingItem == null) {
            return;
        }

        var uiModule = Framework.Instance()->GetUiModule();
        var vf35 = (delegate* unmanaged<UIModule*, IntPtr>) uiModule->vfunc[35];
        var a1 = vf35(uiModule);
        this._searchForRecipesUsingItem(a1, itemId);
    }

    internal void SearchForItem(uint itemId) {
        if (this._searchForItem == null) {
            return;
        }

        var itemFinder = Framework.Instance()->GetUiModule()->GetItemFinderModule();
        this._searchForItem(itemFinder, itemId, 1);
    }
}

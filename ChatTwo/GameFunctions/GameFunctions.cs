using System.Globalization;
using System.Runtime.InteropServices;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.Text.SeStringHandling.Payloads;
using Dalamud.Hooking;
using Dalamud.Memory;
using Dalamud.Utility.Signatures;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Client.UI.Info;
using FFXIVClientStructs.FFXIV.Component.GUI;
using Lumina.Excel.GeneratedSheets;
using ValueType = FFXIVClientStructs.FFXIV.Component.GUI.ValueType;

namespace ChatTwo.GameFunctions;

internal unsafe class GameFunctions : IDisposable
{
    #region Hooks
    private delegate nint ResolveTextCommandPlaceholderDelegate(nint a1, byte* placeholderText, byte a3, byte a4);

    [Signature("E8 ?? ?? ?? ?? 49 8D 4F 18 4C 8B E0", DetourName = nameof(ResolveTextCommandPlaceholderDetour))]
    private Hook<ResolveTextCommandPlaceholderDelegate>? ResolveTextCommandPlaceholderHook { get; init; }
    #endregion

    private Plugin Plugin { get; }
    internal Chat Chat { get; }

    internal GameFunctions(Plugin plugin)
    {
        Plugin = plugin;
        Chat = new Chat(Plugin);

        Plugin.GameInteropProvider.InitializeFromAttributes(this);

        ResolveTextCommandPlaceholderHook?.Enable();
    }

    public void Dispose()
    {
        Chat.Dispose();

        ResolveTextCommandPlaceholderHook?.Dispose();

        Marshal.FreeHGlobal(PlaceholderNamePtr);
    }

    internal nint GetInfoProxyByIndex(InfoProxyId proxyId)
    {
        var infoModule = InfoModule.Instance();
        if (infoModule == null)
            return nint.Zero;

        return (nint) infoModule->GetInfoProxyById(proxyId);
    }

    internal void SendFriendRequest(string name, ushort world)
    {
        ListCommand(name, world, "friendlist");
    }

    internal void AddToBlacklist(string name, ushort world)
    {
        ListCommand(name, world, "blist");
    }

    private void ListCommand(string name, ushort world, string commandName)
    {
        var row = Plugin.DataManager.GetExcelSheet<World>()!.GetRow(world);
        if (row == null)
            return;

        var worldName = row.Name.RawString;
        ReplacementName = $"{name}@{worldName}";
        Plugin.Common.SendMessage($"/{commandName} add {Placeholder}");
    }

    internal static T* GetAddon<T>(string name) where T : unmanaged
    {
        var addon = RaptureAtkModule.Instance()->RaptureAtkUnitManager.GetAddonByName(name);
        return addon != null && addon->IsReady ? (T*)addon : null;
    }

    internal static void SetAddonInteractable(string name, bool interactable)
    {
        var addon = GetAddon<AtkUnitBase>(name);
        if (addon == null)
            return;
        addon->IsVisible = interactable;
    }

    internal static void SetChatInteractable(bool interactable)
    {
        for (var i = 0; i < 4; i++)
            SetAddonInteractable($"ChatLogPanel_{i}", interactable);

        SetAddonInteractable("ChatLog", interactable);
    }

    internal static bool IsAddonInteractable(string name)
    {
        var addon = GetAddon<AtkUnitBase>(name);
        return addon != null && addon->IsVisible;
    }

    internal static void OpenItemTooltip(uint id, ItemPayload.ItemKind itemKind)
    {
        var atkStage = AtkStage.Instance();
        var agent = AgentItemDetail.Instance();
        var addon = GetAddon<AtkUnitBase>("ItemDetail");

        // atkStage ain't gonna be null or we have bigger problems
        if (agent == null || addon == null)
            return;

        var agentPtr = (nint) agent;
        // addresses mentioned here are 7.0
        // see the call near the end of AgentItemDetail.Update
        // offsets valid as of 7.0

        // Switch goes down to default, which is what we want
        *(byte*) (agentPtr + 0x118) = itemKind == ItemPayload.ItemKind.EventItem ? (byte)8 : (byte)1;
        // Item id when hovering over item in chat
        *(uint*) (agentPtr + 0x11C) = id;
        // Always 0 when hovering over item in chat
        *(uint*) (agentPtr + 0x120) = 0;
        // Skips a check to do with inventory
        *(byte*) (agentPtr + 0x128) &= 0xEF;
        // Is also set to the ID of the item when in chat
        agent->ItemId = id;
        // When set to 1, lets everything continue (one frame)
        *(byte*) (agentPtr + 0x1B2) = 1;
        // Skips early return
        *(byte*) (agentPtr + 0x1B6) = 0;

        // This just probably needs to be set
        agent->AddonId = addon->Id;

        // Skips early return
        *(byte*) ((nint) atkStage + 0x2B4) |= 2;
        addon->Show(false, 15);
    }

    internal static void CloseItemTooltip()
    {
        // hide addon first to prevent the "addon close" sound
        var addon = GetAddon<AtkUnitBase>("ItemDetail");
        if (addon != null)
            addon->Hide(true, false, 0);

        var agent = AgentItemDetail.Instance();
        if (agent != null)
        {
            var eventData = stackalloc AtkValue[1];
            var atkValues = stackalloc AtkValue[1];
            atkValues->Type = ValueType.Int;
            atkValues->Int = -1;
            agent->ReceiveEvent(eventData, atkValues, 1, 1);
        }
    }

    internal static void OpenPartyFinder()
    {
        // this whole method: 6.05: 84433A (FF 97 ?? ?? ?? ?? 41 B4 01)
        var lfg = AgentLookingForGroup.Instance();
        if (lfg->IsAgentActive())
        {
            var addonId = lfg->GetAddonId();
            var atkModule = RaptureAtkModule.Instance();
            var atkModuleVtbl = (void**) atkModule->AtkModule.VirtualTable;
            var vf27 = (delegate* unmanaged<RaptureAtkModule*, ulong, ulong, byte>) atkModuleVtbl[27];
            vf27(atkModule, addonId, 1);
        }
        else
        {
            // 6.05: 8443DD
            if (*(uint*) ((nint) lfg + 0x2C20) > 0)
                lfg->Hide();
            else
                lfg->Show();
        }
    }

    internal static bool IsMentor()
    {
        return PlayerState.Instance()->IsMentor();
    }

    internal static InfoProxyCommonList.CharacterData[] GetFriends()
    {
        return InfoProxyFriendList.Instance()->CharDataSpan.ToArray();
    }

    internal static void OpenQuestLog(Quest quest)
    {
        var splits = quest.Id.RawString.Split("_");
        if (splits.Length != 2)
        {
            Plugin.ChatGui.Print("QuestId is wrongly formatted");
            return;
        }

        if (!uint.TryParse(splits[1], NumberStyles.Any, CultureInfo.InvariantCulture,  out var questId))
        {
            Plugin.ChatGui.Print("Unable to parse quest id");
            return;
        }

        AgentQuestJournal.Instance()->OpenForQuest(questId, 1);
    }

    internal static void OpenPartyFinder(uint id)
    {
        AgentLookingForGroup.Instance()->OpenListing(id);
    }

    internal static void OpenAchievement(uint id)
    {
        AgentAchievement.Instance()->OpenById(id);
    }

    internal static bool IsInInstance()
    {
        return Plugin.Condition[ConditionFlag.BoundByDuty56];
    }

    internal static bool TryOpenAdventurerPlate(ulong playerId)
    {
        try
        {
            AgentCharaCard.Instance()->OpenCharaCard(playerId);
            return true;
        }
        catch (Exception e)
        {
            Plugin.Log.Warning(e, "Unable to open adventurer plate");
            return false;
        }
    }

    internal static void ClickNoviceNetworkButton()
    {
        var agent = AgentChatLog.Instance();
        // case 3
        var value = new AtkValue { Type = ValueType.Int, Int = 3, };
        var result = 0;
        var vf0 = *(delegate* unmanaged<AgentChatLog*, int*, AtkValue*, ulong, ulong, int*>*) agent->VirtualTable;
        vf0(agent, &result, &value, 0, 0);
    }

    private readonly nint PlaceholderNamePtr = Marshal.AllocHGlobal(128);
    private readonly string Placeholder = $"<{Guid.NewGuid():N}>";
    private string? ReplacementName;

    private nint ResolveTextCommandPlaceholderDetour(nint a1, byte* placeholderText, byte a3, byte a4)
    {
        var placeholder = MemoryHelper.ReadStringNullTerminated((nint) placeholderText);
        if (ReplacementName == null || placeholder != Placeholder)
            return ResolveTextCommandPlaceholderHook!.Original(a1, placeholderText, a3, a4);

        MemoryHelper.WriteString(PlaceholderNamePtr, ReplacementName);
        ReplacementName = null;

        return PlaceholderNamePtr;
    }
}

using System;
using System.Collections.Generic;
using System.Linq;
using ChooseYourTroops;
using HarmonyLib;
using NavalDLC;
using NavalDLC.Missions.MissionLogics;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.GameMenus;
using TaleWorlds.CampaignSystem.MapEvents;
using TaleWorlds.CampaignSystem.Party;
using TaleWorlds.CampaignSystem.Roster;
using TaleWorlds.Core;
using TaleWorlds.Library;
using TaleWorlds.MountAndBlade;


namespace ChooseYourTroopsWS;

/**
 * Mirrors how NavalMissions.OpenNavalBattleMission builds the player team: its parties, its ships and how many troops
 * DefaultNavalMissionAgentSpawnLogic accepts in it. The naval battle doesn't use the battle size to decide that: the
 * team takes troops until it reaches the crew capacity (TotalCrewCapacity) of its (up to 8) largest ships. The battle
 * size only splits how many of them are on deck at the same time; the rest stay as reserves of the ships.
 */
internal static class NavalPlayerTeam
{
    internal static MBList<MapEventParty> GetParties(MapEvent mapEvent)
    {
        NavalDLCManager.Instance.GameModels.ShipDeploymentModel.GetMapEventPartiesOfPlayerTeams(
            mapEvent.GetMapEventSide(mapEvent.PlayerSide).Parties, mapEvent.IsPlayerSergeant(),
            out _, out var playerTeamParties, out _);
        return playerTeamParties;
    }

    internal static int GetTroopLimit(MapEvent mapEvent)
    {
        var shipDeploymentModel = NavalDLCManager.Instance.GameModels.ShipDeploymentModel;

        shipDeploymentModel.GetMapEventPartiesOfPlayerTeams(
            mapEvent.GetMapEventSide(mapEvent.PlayerSide).Parties, mapEvent.IsPlayerSergeant(),
            out var playerMapEventParty, out var playerTeamParties, out var playerAllyTeamParties);
        shipDeploymentModel.GetShipDeploymentLimitsOfPlayerTeams(playerTeamParties, playerAllyTeamParties,
            out var playerTeamDeploymentLimit, out _);

        var playerTeamShips = new MBList<IShipOrigin>();
        var suitablePlayerShip = shipDeploymentModel.GetSuitablePlayerShip(playerMapEventParty, playerTeamParties);
        if (suitablePlayerShip != null)
            playerTeamShips.Add(suitablePlayerShip);

        shipDeploymentModel.FillShipsOfTeamParties(playerTeamParties, playerTeamDeploymentLimit, playerTeamShips);

        return shipDeploymentModel.GetMaximumDeployableTroopCountForTeam(playerTeamShips, isPlayerTeam: true);
    }

    internal static TroopRoster GetRoster(MBList<MapEventParty> playerTeamParties)
    {
        var roster = TroopRoster.CreateDummyTroopRoster();
        foreach (var mapEventParty in playerTeamParties)
            roster.Add(mapEventParty.Party.MemberRoster);
        return roster;
    }
}

[HarmonyPatch(typeof(MapEventSide), "AllocateTroop")]
public static class MapEventSideAllocateTroopPatch
{
    private static HashSet<string>? _selectedTroops;
    private static HashSet<PartyBase>? _playerTeamParties;
    internal static bool SetupSide = false;

    internal static void Reset()
    {
        _selectedTroops = null;
        _playerTeamParties = null;
    }

    [HarmonyPrefix]
    static void Prefix(
        ref Func<UniqueTroopDescriptor, MapEventParty, bool> customAllocationConditions,
        UniqueTroopDescriptor troopDescriptor,
        MapEventSide __instance)
    {
        if (!SetupSide) return;
        if (!__instance.MapEvent.IsNavalMapEvent) return;
        if (ChooseYourTroopsBehavior.ActualTroopRoster == null) return;
        if (__instance.MapEvent.IsPlayerSimulation) return;
        if (__instance.MissionSide != ChooseYourTroopsBehavior.PlayerSide) return;

        if (_selectedTroops == null)
        {
            _selectedTroops = new HashSet<string>();
            foreach (var troop in ChooseYourTroopsBehavior.ActualTroopRoster.GetTroopRoster())
            {
                if (troop.Character == null) continue;
                int healthy = troop.Number - troop.WoundedNumber;
                if (healthy <= 0) continue;
                _selectedTroops.Add(troop.Character.StringId);
            }
        }

        _playerTeamParties ??= new HashSet<PartyBase>(
            NavalPlayerTeam.GetParties(__instance.MapEvent).Select(mapEventParty => mapEventParty.Party));

        var selectedTroops = _selectedTroops;
        var playerTeamParties = _playerTeamParties;

        customAllocationConditions = (descriptor, party) =>
        {
            try
            {
                // Troops of the ally team go to its own ships and limit: the selection is only for the player team
                if (!playerTeamParties.Contains(party.Party)) return true;

                CharacterObject troop = party.Troops[descriptor].Troop;
                if (troop == null) return false;
                return selectedTroops.Contains(troop.StringId);
            }
            catch
            {
                return false;
            }
        };
    }
}

[HarmonyPatch(typeof(DefaultNavalMissionAgentSpawnLogic))]
public static class DefaultNavalMissionAgentSpawnLogicPatches
{
    [HarmonyPatch("EarlyStart")]
    [HarmonyPrefix]
    static void EarlyStartPrefix()
    {
        // Rebuilt for every mission, so a new selection (or another battle) is never filtered with an old one
        MapEventSideAllocateTroopPatch.Reset();
        MapEventSideAllocateTroopPatch.SetupSide = true;
    }

    [HarmonyPatch("OnDeploymentFinished")]
    [HarmonyPrefix]
    static void OnDeploymentFinishedPrefix()
    {
        MapEventSideAllocateTroopPatch.SetupSide = false;
        MapEventSideAllocateTroopPatch.Reset();
    }

    [HarmonyPatch("OnMissionStateFinalized")]
    [HarmonyPrefix]
    static void OnMissionStateFinalizedPrefix()
    {
        MapEventSideAllocateTroopPatch.SetupSide = false;
        MapEventSideAllocateTroopPatch.Reset();
    }
}


public class ChooseYourTroopsWSBehavior : CampaignBehaviorBase
{
    public override void RegisterEvents()
    {
        CampaignEvents.OnAfterSessionLaunchedEvent.AddNonSerializedListener(this,
            AddGamesMenu);
    }

    private void AddGamesMenu(CampaignGameStarter gameSystemInitializer)
    {
        // Its attack option also goes through MenuHelper.EncounterAttackConsequence -> OpenNavalBattleMission
        gameSystemInitializer.AddGameMenuOption("naval_storyline_encounter", "select_troops",
            "{=select_your_troops_text}Select troops ({SELECTED_AMOUNT} selected).",
            ChooseYourTroopsBehavior.SelectTroopsCondition, SelectTroopsConsequence);

        gameSystemInitializer.AddGameMenuOption("encounter", "select_troops",
            "{=select_your_troops_text}Select troops ({SELECTED_AMOUNT} selected).",
            args => ChooseYourTroopsBehavior.OnNavalMapEvent && ChooseYourTroopsBehavior.SelectTroopsCondition(args),
            SelectTroopsConsequence);
    }

    private static void SelectTroopsConsequence(MenuCallbackArgs args)
    {
        var mapEvent = MapEvent.PlayerMapEvent;
        var playerTeamParties = NavalPlayerTeam.GetParties(mapEvent);
        var playerTeamRoster = NavalPlayerTeam.GetRoster(playerTeamParties);

        int maximum;
        try
        {
            maximum = NavalPlayerTeam.GetTroopLimit(mapEvent);
        }
        catch (Exception ex)
        {
            InformationManager.DisplayMessage(
                new InformationMessage($"ChooseYourTroopsWS: couldn't compute the ships' troop limit\n{ex.Message}"));
            maximum = 0;
        }

        // No ships to compute it from: don't block the selection
        if (maximum <= 0)
            maximum = playerTeamRoster.TotalHealthyCount;

        ChooseYourTroopsBehavior.OpenNavalTroopsSelector(args, maximum, playerTeamRoster);
    }

    public override void SyncData(IDataStore dataStore) {}
}


public class SubModule : MBSubModuleBase
{
    private Harmony _harmony = null!;

    protected override void OnSubModuleLoad()
    {
        base.OnSubModuleLoad();
        _harmony = new Harmony("choose_your_troops_ws");
        _harmony.PatchAll();
    }

    protected override void OnGameStart(Game game, IGameStarter gameStarterObject)
    {
        base.OnCampaignStart(game, gameStarterObject);

        if (gameStarterObject is not CampaignGameStarter campaignGameStarter)
        {
            return;
        }

        campaignGameStarter.AddBehavior(new ChooseYourTroopsWSBehavior());
    }
}

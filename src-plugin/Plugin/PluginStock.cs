
namespace K4Arenas
{
	using CounterStrikeSharp.API;
	using CounterStrikeSharp.API.Core;
	using CounterStrikeSharp.API.Modules.Utils;

	using CounterStrikeSharp.API.Modules.Commands;

	using K4Arenas.Models;
	using CounterStrikeSharp.API.Modules.Admin;
	using CounterStrikeSharp.API.Modules.Cvars;
	using System.Data;
	using CounterStrikeSharp.API.Modules.Entities.Constants;
	using System.Runtime.InteropServices;

	public sealed partial class Plugin : BasePlugin
	{
		public void TerminateRoundIfPossible(CsTeam? team = null)
		{
			if (gameRules is null || gameRules.WarmupPeriod == true)
				return;

			if (Arenas is null)
				return;

			List<CCSPlayerController> players = Utilities.GetPlayers().Where(x => x?.IsValid == true && x.PlayerPawn?.IsValid == true && x.Connected == PlayerConnectedState.PlayerConnected).ToList();

			if (players.Count(p => !p.IsBot) == 0)
				return;

			List<CCSPlayerController> alivePlayers = players.Where(x => x?.IsValid == true && x.PlayerPawn?.IsValid == true && x.PlayerPawn.Value?.Health > 0).ToList();

			if (alivePlayers.Count == 0 || (team != null && alivePlayers.Count(x => x.Team == team) == 0))
				return;

			bool hasOnGoingArena = false;
			bool hasWaitingPlayers = WaitingArenaPlayers.Any(p => p.IsValid && !p.AFK && !p.Controller.IsBot);

			foreach (Arena arena in Arenas.ArenaList)
			{
				if (arena.IsActive && (arena.HasRealPlayers || !hasWaitingPlayers))
				{
					hasOnGoingArena = true;
					break;
				}
			}

			if (!hasOnGoingArena)
			{
				int tCount = alivePlayers.Count(x => x.Team == CsTeam.Terrorist);
				int ctCount = alivePlayers.Count(x => x.Team == CsTeam.CounterTerrorist);

				Server.NextFrame(() =>
				{
					gameRules.TerminateRound(3, tCount > ctCount ? RoundEndReason.TerroristsWin : ctCount > tCount ? RoundEndReason.CTsWin : RoundEndReason.RoundDraw);
				});
			}
		}

		public ArenaPlayer? SetupPlayer(CCSPlayerController playerController)
		{
			if (gameRules?.WarmupPeriod == false)
				playerController.ChangeTeam(CsTeam.Spectator);

			playerController.Clan = $"{Localizer[gameRules?.WarmupPeriod == true ? "k4.general.warmup" : "k4.general.waiting"]} |";
			Utilities.SetStateChanged(playerController, "CCSPlayerController", "m_szClan");

			ArenaPlayer arenaPlayer = new ArenaPlayer(this, playerController);
			WaitingArenaPlayers.Enqueue(arenaPlayer);

			arenaPlayer.Controller.PrintToChat($" {Localizer["k4.general.prefix"]} {Localizer["k4.chat.queue_added", WaitingArenaPlayers.Count]}");

			ulong steamID = playerController.SteamID;
			Task.Run(async () =>
			{
				await LoadPlayerAsync(steamID);
			});

			return arenaPlayer;
		}

		public bool CommandHelper(CCSPlayerController? player, CommandInfo info, CommandUsage usage, int argCount = 0, string? help = null, string? permission = null)
		{
			switch (usage)
			{
				case CommandUsage.CLIENT_ONLY:
					if (player == null || !player.IsValid || player.PlayerPawn.Value == null)
					{
						info.ReplyToCommand($" {Localizer["k4.general.prefix"]} {Localizer["k4.general.commandclientonly"]}");
						return false;
					}
					break;
				case CommandUsage.SERVER_ONLY:
					if (player != null)
					{
						info.ReplyToCommand($" {Localizer["k4.general.prefix"]} {Localizer["k4.general.commandserveronly"]}");
						return false;
					}
					break;
				case CommandUsage.CLIENT_AND_SERVER:
					if (!(player == null || (player != null && player.IsValid && player.PlayerPawn.Value != null)))
						return false;
					break;
			}

			if (permission != null && permission.Length > 0)
			{
				if (player != null && !AdminManager.PlayerHasPermissions(player, permission))
				{
					info.ReplyToCommand($" {Localizer["k4.general.prefix"]} {Localizer["k4.general.commandnoperm"]}");
					return false;
				}
			}

			if (argCount > 0 && help != null)
			{
				int checkArgCount = argCount + 1;
				if (info.ArgCount < checkArgCount)
				{
					info.ReplyToCommand($" {Localizer["k4.general.prefix"]} {Localizer["k4.general.commandhelp", info.ArgByIndex(0).Replace("css_", string.Empty), help]}");
					return false;
				}
			}

			return true;
		}

		public void CheckCommonProblems()
		{
			// Common things that fuck up the gameplay
			Server.ExecuteCommand("mp_halftime 0");
			Server.ExecuteCommand("mp_join_grace_time 0");


			// We handle weapons dont worry
			Server.ExecuteCommand("mp_t_default_secondary \"\"");
			Server.ExecuteCommand("mp_ct_default_secondary \"\"");
			Server.ExecuteCommand("mp_t_default_primary \"\"");
			Server.ExecuteCommand("mp_ct_default_primary \"\"");

			// This could cause problems with the weapons
			Server.ExecuteCommand("mp_equipment_reset_rounds 0");
		}

		public RoundType GetCommonRoundType(List<RoundType>? roundPreferences1, List<RoundType>? roundPreferences2, bool multi)
		{
			List<RoundType> commonRounds = roundPreferences1?.Intersect(roundPreferences2 ?? Enumerable.Empty<RoundType>())?.ToList() ?? new List<RoundType>();
			List<RoundType> commonUsableRounds = multi ? commonRounds : commonRounds.Where(rt => rt.TeamSize < 2).ToList();

			if (commonUsableRounds.Any())
			{
				return commonUsableRounds[rng.Next(0, commonUsableRounds.Count)];
			}

			List<RoundType> availableRoundTypes = RoundType.RoundTypes.Where(rt => rt.TeamSize < 2).ToList();
			return availableRoundTypes[rng.Next(0, availableRoundTypes.Count)];
		}

		public static void MoveQueue<T>(Queue<T> from, Queue<T> to)
		{
			while (from.Count > 0)
			{
				T item = from.Dequeue();
				to.Enqueue(item);
			}
		}

		public void MoveEndedChallengesToQueue(Queue<ArenaPlayer> rankedPlayers)
		{
			var endedChallenges = Arenas!.ArenaList
				.Where(a => a.Team1 != null && a.Team2 != null)
				.Select(a => a.Team1!.First().Challenge)
				.Where(c => c != null && c.IsEnded)
				.Distinct();

			List<ArenaPlayer> rankedList = rankedPlayers.ToList();

			foreach (ChallengeModel? endedChallenge in endedChallenges)
			{
				if (endedChallenge == null)
					continue;

				ProcessPlayer(endedChallenge.Player1, endedChallenge.Player1Placement, ref rankedList);
				ProcessPlayer(endedChallenge.Player2, endedChallenge.Player2Placement, ref rankedList);
			}

			rankedPlayers = new Queue<ArenaPlayer>(rankedList);

			void ProcessPlayer(ArenaPlayer player, int placement, ref List<ArenaPlayer> rankedList)
			{
				if (!player.IsValid)
					return;

				if (placement > rankedList.Count)
				{
					rankedList.Add(player);
				}
				else
				{
					rankedList.Insert(placement - 1, player);
				}

				player.Challenge = null;
			}
		}

		public void EnqueueTeamPlayers(List<ArenaPlayer>? team, Queue<ArenaPlayer> queue)
		{
			if (team is null)
				return;

			foreach (ArenaPlayer player in team)
			{
				if (player?.IsValid == true)
				{
					queue.Enqueue(player);
				}
			}
		}

		public string GetOpponentNames(List<ArenaPlayer>? opponents)
		{
			if (opponents is null || opponents.Count == 0)
				return Localizer["k4.general.no_opponent"];


			return string.Join(", ", opponents.Where(p => p.IsValid).Select(p => p.Controller.PlayerName));
		}
	}
}
using System;
using System.Runtime.InteropServices;
using ExileCore;
using ExileCore.PoEMemory.Components;
using ExileCore.PoEMemory.MemoryObjects;
using ExileCore.Shared.Cache;
using SharpDX;
using SharpDX.Direct3D11;
using SharpDX.DXGI;
using System.Collections.Generic;
using Map = ExileCore.PoEMemory.Elements.Map;
using EpPathFinding.cs;
using System.Linq;
using System.IO;
using System.Threading;
using System.Collections.Concurrent;
using System.Drawing;

namespace SimpleFllw
{
	/// <summary>
	/// All work is shamelessly leached and cobbled together. 
	///		Follower: 13413j1j13j5315n13
	///		Terrain: mm3141
	///		Pathfinding: juhgiyo
	///	I'm just linking things together and doing silly experiments. 
	/// </summary>
	internal class SimpleFllwCore : BaseSettingsPlugin<SimpleFllwSettings>
	{
		private Random random = new Random();
		private Camera Camera => GameController.Game.IngameState.Camera;		
		private Dictionary<uint, Entity> _areaTransitions = new Dictionary<uint, Entity>();
		
		private Vector3 _lastTargetPosition;
		private Vector3 _lastPlayerPosition;
		private volatile Entity _followTarget;

		private bool _hasUsedWP = false;


		private List<TaskNode> _tasks = new List<TaskNode>();
		private DateTime _nextBotAction = DateTime.Now;

		private int _numRows, _numCols;
		private byte[,] _tiles;
		public SimpleFllwCore()
		{
			Name = "SimpleFllw";
		}

		public override bool Initialise()
		{
			Input.RegisterKey(Settings.MovementKey.Value);

			Input.RegisterKey(Settings.ToggleFollower.Value);
			Settings.ToggleFollower.OnValueChanged += () => { Input.RegisterKey(Settings.ToggleFollower.Value); };

			Input.RegisterKey(Settings.ClearTasksTransitionKey.Value);

			_followTarget = null;

			return base.Initialise();
		}


		/// <summary>
		/// Clears all pathfinding values. Used on area transitions primarily.
		/// </summary>
		private void ResetPathing()
		{
			_tasks = new List<TaskNode>();
			_followTarget = null;
			_lastTargetPosition = Vector3.Zero;
			_lastPlayerPosition = Vector3.Zero;
			
			_hasUsedWP = false;
		}

		public override void AreaChange(AreaInstance area)
		{
			_followTarget = null;
			ResetPathing();

			//Load initial transitions!

			ResetTransitions();
		}

		private void ResetTransitions()
		{
			_areaTransitions = new Dictionary<uint, Entity>();

			if (GameController.EntityListWrapper.ValidEntitiesByType.TryGetValue(ExileCore.Shared.Enums.EntityType.AreaTransition, out ConcurrentBag<Entity> areaTransitions))
			{
				ResetTransitionsHelper(areaTransitions);
			}

			if (GameController.EntityListWrapper.ValidEntitiesByType.TryGetValue(ExileCore.Shared.Enums.EntityType.Portal, out ConcurrentBag<Entity> portals))
			{
				ResetTransitionsHelper(portals, true);
			}

			if (GameController.EntityListWrapper.ValidEntitiesByType.TryGetValue(ExileCore.Shared.Enums.EntityType.TownPortal, out ConcurrentBag<Entity> townPortals))
			{
				ResetTransitionsHelper(townPortals, true);
			}

			if (GameController.EntityListWrapper.ValidEntitiesByType.TryGetValue(ExileCore.Shared.Enums.EntityType.IngameIcon, out ConcurrentBag<Entity> ingameIcons))
			{
				ResetTransitionHeistHelper(ingameIcons);
			}

			if (GameController.EntityListWrapper.ValidEntitiesByType.TryGetValue(ExileCore.Shared.Enums.EntityType.MiscellaneousObjects, out ConcurrentBag<Entity> misc))
			{
				ResetTransitionHarvestHelper(misc);
			}
		}

		private void ResetTransitionsHelper(ConcurrentBag<Entity> transitions, bool portals = false)
		{
			foreach (var transition in transitions)
			{
				if (!_areaTransitions.ContainsKey(transition.Id) && !transition.Metadata.Contains("Objects/MapPortal"))
					_areaTransitions.Add(transition.Id, transition);
			}
		}

		private void ResetTransitionHeistHelper(ConcurrentBag<Entity> transitions)
		{
			foreach (var transition in transitions)
			{
				if (!_areaTransitions.ContainsKey(transition.Id) && transition.IsTargetable)
				{
					if (transition.Metadata == "Metadata/Terrain/Leagues/Heist/Objects/MissionEntryPortal"
						|| transition.Metadata == "Metadata/Terrain/Leagues/Heist/Objects/MissionExitPortal"
						|| transition.Metadata == "Metadata/MiscellaneousObjects/AreaTransition")
					_areaTransitions.Add(transition.Id, transition);
				}
					
			}
		}

		private void ResetTransitionHarvestHelper(ConcurrentBag<Entity> transitions)
		{
			foreach (var transition in transitions)
			{
				if (!_areaTransitions.ContainsKey(transition.Id) && transition.IsTargetable)
				{
					if (transition.Metadata == "Metadata/Terrain/Leagues/Harvest/Objects/HarvestPortalToggleableReverse"
						|| transition.Metadata == "Metadata/Terrain/Leagues/Harvest/Objects/HarvestPortalToggleableReverseReturn")
						_areaTransitions.Add(transition.Id, transition);
				}

			}
		}

		private void MouseoverItem(Entity item)
		{
			var uiLoot = GameController.IngameState.IngameUi.ItemsOnGroundLabels.FirstOrDefault(I => I.IsVisible && I.ItemOnGround.Id == item.Id);
			if (uiLoot != null)
			{
				var clickPos = uiLoot.Label.GetClientRect().Center;
				Mouse.SetCursorPos(new Vector2(
					clickPos.X + random.Next(-15, 15),
					clickPos.Y + random.Next(-10, 10)));
				Thread.Sleep(30 + random.Next(Settings.BotInputFrequency));				
			}
		}

		public override Job Tick()
		{
			try
			{
				if (Settings.ToggleFollower.PressedOnce())
				{
					Settings.IsFollowEnabled.SetValueNoEvent(!Settings.IsFollowEnabled.Value);
					_tasks = new List<TaskNode>();
				}

				// Close ritual window if opened
				if (GameController.Game.IngameState.IngameUi.RitualWindow.IsVisible)
				{
					Input.KeyUp(System.Windows.Forms.Keys.V);
					Thread.Sleep(random.Next(25) + 30);
					Input.KeyDown(System.Windows.Forms.Keys.V);
					Thread.Sleep(random.Next(25) + 30);
					Input.KeyUp(System.Windows.Forms.Keys.V);
				}

				// Dont run logic if ultimatum panel is visible
				// turn off for now

				/*
				int ultimatumPanelIndex = 93;
				IngameUIElements igu = GameController?.Game?.IngameState?.IngameUi;

				if (igu?.Children != null && igu.Children.Count > ultimatumPanelIndex && igu.Children[ultimatumPanelIndex].IsVisible)
				{
					Input.KeyUp(Settings.MovementKey);
					Input.KeyUp(Settings.DashKey);
					return null;
				} 
				*/

				//Dont run logic if we're dead!
				if (!GameController.Player.IsAlive)
				{
					Input.KeyUp(Settings.MovementKey);
					Input.KeyUp(Settings.DashKey);
					return null;
				}

				if (!Settings.IsFollowEnabled.Value)
				{
					Input.KeyUp(Settings.MovementKey);
					Input.KeyUp(Settings.DashKey);
					return null;
				}

				var _pathfindingDistance = Settings.PathfindingNodeDistance.Value;
				var _dt = Settings.PathfindingNodeDistance.Value * 3;

				if (Settings.ClearTasksTransitionKey.PressedOnce())
				{
					//LogMessage(" >> transition key pressed !");
					ResetTransitions();
					//LogMessage(" >> transitions: " + _areaTransitions.Count);

					_tasks = new List<TaskNode>();

					var transOptions = _areaTransitions.Values.
						Where(I => Vector3.Distance(_lastTargetPosition, I.Pos) < Settings.ClearPathDistance).
						OrderBy(I => Vector3.Distance(_lastTargetPosition, I.Pos)).ToArray();
					var _portalsCount = 0;

					foreach (Entity _to in transOptions)
					{
						if (_to.Type == ExileCore.Shared.Enums.EntityType.Portal || _to.Type == ExileCore.Shared.Enums.EntityType.TownPortal)
						{
							_portalsCount++;
						}
					}

					if (transOptions.Length > 0 && _portalsCount >= 4)
					{
						int transNumber = 0;

						transNumber = Settings.SlotNumber + 1;

						if (transNumber >= transOptions.Length)
						{
							transNumber = transOptions.Length - 1;
						}

						var tr = transOptions[transNumber];

						if (tr.HasComponent<Render>())
						{
							var bounds = tr.GetComponent<Render>().Bounds;
							if (tr.Metadata == "Metadata/Terrain/Leagues/Heist/Objects/MissionExitPortal")
							{
								
								_tasks.Add(new TaskNode(tr.Pos, _pathfindingDistance, TaskNodeType.Transition, new Vector3(bounds.X, bounds.Y, 140)));
							}
							else 
							{ 
								_tasks.Add(new TaskNode(tr.Pos, _pathfindingDistance, TaskNodeType.Transition, bounds));
							}
						}
						else
						{
							_tasks.Add(new TaskNode(tr.Pos, _pathfindingDistance, TaskNodeType.Transition));
						}
					}
					else
					{
						var tr = _areaTransitions.Values.OrderBy(I => Vector3.Distance(GameController.Player.Pos, I.Pos)).FirstOrDefault();
						var dist = Vector3.Distance(GameController.Player.Pos, tr.Pos);
						if (dist < Settings.ClearPathDistance.Value)
						{
							if (tr.HasComponent<Render>())
							{
								var bounds = tr.GetComponent<Render>().Bounds;
								if (tr.Metadata == "Metadata/Terrain/Leagues/Heist/Objects/MissionExitPortal")
								{

									_tasks.Add(new TaskNode(tr.Pos, 200, TaskNodeType.Transition, new Vector3(bounds.X, bounds.Y, 140)));
								}
								else
								{
									_tasks.Add(new TaskNode(tr.Pos, 200, TaskNodeType.Transition, bounds));
								}
							}
							else
							{
								_tasks.Add(new TaskNode(tr.Pos, 200, TaskNodeType.Transition));
							}
						}
					}
				}

				if (GameController.Area.CurrentArea.IsHideout)
				{
					_pathfindingDistance = (int)((float)_pathfindingDistance * 4);
				}
				else
				{
					foreach (var _t in _areaTransitions)
					{
						if (_t.Value?.Pos != null && GameController?.Player?.Pos != null)
						{
							if (Vector3.Distance(_t.Value.Pos, GameController.Player.Pos) <= _dt)
							{
								_pathfindingDistance = (int)((float)_pathfindingDistance * 2);
								break;
							}
						}
					}
				}

				//Cache the current follow target (if present)
				_followTarget = GetFollowingTarget();
				if (_followTarget != null)
				{
					var distanceFromFollower = Vector3.Distance(GameController.Player.Pos, _followTarget.Pos);
					//We are NOT within clear path distance range of leader. Logic can continue
					if (distanceFromFollower >= Settings.ClearPathDistance.Value)
					{
						//Leader moved VERY far in one frame. Check for transition to use to follow them.
						var distanceMoved = Vector3.Distance(_lastTargetPosition, _followTarget.Pos);
						if (_lastTargetPosition != Vector3.Zero && distanceMoved > Settings.ClearPathDistance.Value)
						{
							/*var transition = _areaTransitions.Values.OrderBy(I => Vector3.Distance(_lastTargetPosition, I.Pos)).FirstOrDefault();
							var dist = Vector3.Distance(_lastTargetPosition, transition.Pos);
							if (dist < Settings.ClearPathDistance.Value)
								_tasks.Add(new TaskNode(transition.Pos, 200, TaskNodeType.Transition));*/
						}
						//We have no path, set us to go to leader pos.
						else if (_tasks.Count == 0)
						{
							_tasks.Add(new TaskNode(_followTarget.Pos, Settings.PathfindingNodeDistance));
						}
						//We have a path. Check if the last task is far enough away from current one to add a new task node.
						else
						{
							var distanceFromLastTask = Vector3.Distance(_tasks.Last().WorldPosition, _followTarget.Pos);
							if (distanceFromLastTask >= Settings.PathfindingNodeDistance)
								_tasks.Add(new TaskNode(_followTarget.Pos, Settings.PathfindingNodeDistance));
						}
					}
					else
					{
						//Clear all tasks except for looting/claim portal (as those only get done when we're within range of leader. 
						if (_tasks.Count > 0)
						{
							for (var i = _tasks.Count - 1; i >= 0; i--)
								if (_tasks[i].Type == TaskNodeType.Movement || _tasks[i].Type == TaskNodeType.Transition)
									_tasks.RemoveAt(i);
						}
						else if (Settings.IsCloseFollowEnabled.Value)
						{
							//Close follow logic. We have no current tasks. Check if we should move towards leader
							if (distanceFromFollower >= _pathfindingDistance)
								_tasks.Add(new TaskNode(_followTarget.Pos, Settings.PathfindingNodeDistance));
						}

						//Check if we should add quest loot logic. We're close to leader already
						var questLoot = GetLootableQuestItem();
						if (Settings.IsLootQuestItemsEnabled && questLoot != null &&
							Vector3.Distance(GameController.Player.Pos, questLoot.Pos) < Settings.ClearPathDistance.Value &&
							_tasks.FirstOrDefault(I => I.Type == TaskNodeType.Loot) == null)
							_tasks.Add(new TaskNode(questLoot.Pos, Settings.ClearPathDistance, TaskNodeType.Loot));

						else if (Settings.IsAutoPickUpWaypointEnabled && !_hasUsedWP)
						{
							//Check if there's a waypoint nearby
							Entity waypoint = null;

							if (GameController.EntityListWrapper.ValidEntitiesByType.TryGetValue(ExileCore.Shared.Enums.EntityType.Waypoint, out ConcurrentBag<Entity> waypoints))
							{
								waypoint = waypoints.SingleOrDefault(I => Vector3.Distance(GameController.Player.Pos, I.Pos) < Settings.ClearPathDistance);
							}
							

							if (waypoint != null)
							{
								_hasUsedWP = true;
								_tasks.Add(new TaskNode(waypoint.Pos, Settings.ClearPathDistance, TaskNodeType.ClaimWaypoint));
							}

						}

					}
					_lastTargetPosition = _followTarget.Pos;
				}
				//Leader is null but we have tracked them this map.
				//Try using transition to follow them to their map
				else if (_tasks.Count == 0 &&
					_lastTargetPosition != Vector3.Zero)
				{
					// dont auto use portal (happens randomly on laggy map
					/*
					var transOptions = _areaTransitions.Values.
						Where(I => Vector3.Distance(_lastTargetPosition, I.Pos) < Settings.ClearPathDistance).
						OrderBy(I => Vector3.Distance(_lastTargetPosition, I.Pos)).ToArray();
					if (transOptions.Length > 0)
					{
						int transNumber = 0;

						if (transOptions.Length > 2)
						{
							transNumber = Settings.SlotNumber + 1;

							if (transNumber >= transOptions.Length)
							{
								transNumber = transOptions.Length - 1;
							}

						}
						_tasks.Add(new TaskNode(transOptions[transNumber].Pos, _pathfindingDistance, TaskNodeType.Transition));
					}*/
				}

				//Don't run tasks if looting
				if (Input.GetKeyState(Settings.LootKey))
				{
					Input.KeyUp(Settings.MovementKey);
					return null;
				}

				//We have our tasks, now we need to perform in game logic with them.
				if (DateTime.Now > _nextBotAction && _tasks.Count > 0)
				{
					var currentTask = _tasks.First();
					var taskDistance = Vector3.Distance(GameController.Player.Pos, currentTask.WorldPosition);
					var playerDistanceMoved = Vector3.Distance(GameController.Player.Pos, _lastPlayerPosition);

					//We are using a same map transition and have moved significnatly since last tick. Mark the transition task as done.
					if (currentTask.Type == TaskNodeType.Transition &&
						playerDistanceMoved >= Settings.ClearPathDistance.Value)
					{
						_tasks.RemoveAt(0);
						if (_tasks.Count > 0)
							currentTask = _tasks.First();
						else
						{
							_lastPlayerPosition = GameController.Player.Pos;
							return null;
						}
					}

					switch (currentTask.Type)
					{
						case TaskNodeType.Movement:
							//if (GameController.Area.CurrentArea.Area.RawName.Equals("HeistHubEndless"))
							if (GameController.Area.CurrentArea.IsTown)
							{
								Input.KeyUp(Settings.MovementKey);
								return null;
							}

							if (Vector3.Distance(currentTask.WorldPosition, GameController.Player.Pos) > Settings.ClearPathDistance.Value * 1.5)
							{
								Input.KeyUp(Settings.MovementKey);
								_tasks.RemoveAt(0);
								break;
							}

							_nextBotAction = DateTime.Now.AddMilliseconds(Settings.BotInputFrequency + random.Next(Settings.BotInputFrequency));

							Vector3 direction;
							Vector3 normal;

							// Follow offsets
							direction = GameController.Player.Pos - currentTask.WorldPosition;
							direction.Normalize();
							normal = new Vector3(-direction.Y, direction.X, direction.Z);
							direction *= Settings.FollowOffsetDirection;
							normal *= Settings.FollowOffsetNormal;

							Vector3 corrected = currentTask.WorldPosition + direction + normal;

							Mouse.SetCursorPosHuman2(WorldToValidScreenPosition(corrected));
							Thread.Sleep(random.Next(25) + 30);

							if (!Input.GetKeyState(Settings.MovementKey))
							{
								Input.KeyDown(Settings.MovementKey);
							}

							// dashing if enabled and far from target
							if (_followTarget != null && Settings.IsDashEnabled && Vector3.Distance(_followTarget.Pos, GameController.Player.Pos) > _pathfindingDistance * 3.5)
							{
								Input.KeyDown(Settings.DashKey);
								Thread.Sleep(random.Next(25) + 30);
								Input.KeyUp(Settings.DashKey);
							}

							//Within bounding range. Task is complete
							//Note: Was getting stuck on close objects... testing hacky fix.
							if (taskDistance <= _pathfindingDistance * 1.5)
								_tasks.RemoveAt(0);
							break;
						case TaskNodeType.Loot:
							{
								_nextBotAction = DateTime.Now.AddMilliseconds(Settings.BotInputFrequency + random.Next(Settings.BotInputFrequency));
								currentTask.AttemptCount++;
								var questLoot = GetLootableQuestItem();
								if (questLoot == null
									|| currentTask.AttemptCount > 2
									|| Vector3.Distance(GameController.Player.Pos, questLoot.Pos) >= Settings.ClearPathDistance.Value)
									_tasks.RemoveAt(0);

								Input.KeyUp(Settings.MovementKey);
								Thread.Sleep(Settings.BotInputFrequency);
								//Pause for long enough for movement to hopefully be finished.
								var targetInfo = questLoot.GetComponent<Targetable>();
								if (!targetInfo.isTargeted)
									MouseoverItem(questLoot);
								if (targetInfo.isTargeted)
								{
									Input.KeyUp(Settings.MovementKey);
									Thread.Sleep(25);
									Mouse.LeftMouseUp();
									Thread.Sleep(25);
									Mouse.LeftMouseDown();
									Thread.Sleep(25 + random.Next(Settings.BotInputFrequency));
									Mouse.LeftMouseUp();
									_nextBotAction = DateTime.Now.AddSeconds(1);
								}

								break;
							}
						case TaskNodeType.Transition:
							{
								_nextBotAction = DateTime.Now.AddMilliseconds(Settings.BotInputFrequency * 2 + random.Next(Settings.BotInputFrequency));

								//if (taskDistance <= Settings.ClearPathDistance.Value)
								//{

								if (currentTask.AttemptCount == 3)
								{
									Vector3 backDirection = _lastPlayerPosition - currentTask.WorldPosition;
									backDirection.Normalize();
									backDirection *= 130;

									var stepBackScreenPos = WorldToValidScreenPosition(currentTask.WorldPosition + backDirection);
									Input.KeyUp(Settings.MovementKey);
									Mouse.SetCursorPosHuman2(stepBackScreenPos);
									Thread.Sleep(random.Next(25) + 30);
									Input.KeyDown(Settings.MovementKey);
									Thread.Sleep(random.Next(25) + 30);

									_nextBotAction = DateTime.Now.AddSeconds(1);
									Thread.Sleep(random.Next(25) + 600);
									Input.KeyUp(Settings.MovementKey);
								}
								else
								{
									/*var zOffset = -40;

									if (currentTask.HeistExit)
									{
										zOffset = -270;
									}

									if (currentTask.AttemptCount <= 3 && !currentTask.HeistExit)
									{
										zOffset = currentTask.AttemptCount * -55;
									}*/

									var offset = new Vector3(0, 0, 0);

									if (currentTask.ContainsSize)
									{
										// horizontal transition
										if (currentTask.Size.Z < 5)
										{
											switch (Settings.SlotNumber)
											{
												case 0:
													offset.X = (currentTask.Size.X - 10);
													offset.Y = (currentTask.Size.Y - 10);
													break;

												case 1:
													offset.X = -(currentTask.Size.X - 10);
													offset.Y = (currentTask.Size.Y - 10);
													break;

												case 2:
													offset.X = (currentTask.Size.X - 10);
													offset.Y = -(currentTask.Size.Y - 10);
													break;

												case 3:
													offset.X = -(currentTask.Size.X - 10);
													offset.Y = -(currentTask.Size.Y - 10);
													break;

												default:
													offset.X = (currentTask.Size.X - 10);
													offset.Y = (currentTask.Size.Y - 10);
													break;
											}
										}
										// vertical transition
										else
										{
											
											var fix = 15;

											if (GameController.Area.CurrentArea.Name.Equals("Fields"))
											{
												fix = 90;
											}

											offset.Z = -(currentTask.Size.Z * 2 - fix);
										}
									}

									var screenPos = WorldToValidScreenPosition(currentTask.WorldPosition, offset);

									if (screenPos.Y < 60) screenPos.Y = 60;
									
									Input.KeyUp(Settings.MovementKey);

									if (currentTask.AttemptCount == 0)
									{
										// Use Phase run
										Input.KeyDown(Settings.PhaseRunKey);
										Thread.Sleep(random.Next(25) + 30);
										Input.KeyUp(Settings.PhaseRunKey);
									}

									Input.KeyUp(Settings.MovementKey);

									//Click the transition
									Mouse.SetCursorPosAndLeftClickHuman(screenPos, 100);
									//
									_nextBotAction = DateTime.Now.AddSeconds(1);
								}


								/*}
								else
								{
									//Walk towards the transition
									Input.KeyUp(Settings.MovementKey);
									Thread.Sleep(random.Next(25) + 30);
									Mouse.SetCursorPosHuman2(screenPos);
									Thread.Sleep(random.Next(25) + 30);
									Input.KeyDown(Settings.MovementKey);
									Thread.Sleep(random.Next(25) + 30);
									Input.KeyUp(Settings.MovementKey);
								}*/
								currentTask.AttemptCount++;
								if (currentTask.AttemptCount > 5)
								{
									_tasks.RemoveAt(0);
									Input.KeyUp(Settings.MovementKey);
								}
								break;
							}

						case TaskNodeType.ClaimWaypoint:
							{
								if (Vector3.Distance(GameController.Player.Pos, currentTask.WorldPosition) > 150)
								{
									var screenPos = WorldToValidScreenPosition(currentTask.WorldPosition);

									Input.KeyUp(Settings.MovementKey);
									Thread.Sleep(Settings.BotInputFrequency);
									Mouse.SetCursorPosAndLeftClickHuman(screenPos, 100);
									_nextBotAction = DateTime.Now.AddSeconds(1);
								}
								currentTask.AttemptCount++;
								if (currentTask.AttemptCount > 3)
								{
									_tasks.RemoveAt(0);
									Input.KeyUp(Settings.MovementKey);
								}
								break;
							}
					}
				}
				else
				{
					if (GameController?.Player?.Pos == null || _followTarget?.Pos == null)
					{
						Input.KeyUp(Settings.MovementKey);
					}
					else
					{
						var recheckDistance = Vector3.Distance(GameController.Player.Pos, _followTarget.Pos);

						if (recheckDistance <= _pathfindingDistance * 1.3)
						{
							Input.KeyUp(Settings.MovementKey);
						}
					}
				}

				_lastPlayerPosition = GameController.Player.Pos;
				return null;
			}
			catch (NullReferenceException)
			{
				return null;
			}
		}
		

		private Entity GetFollowingTarget()
		{
			var leaderName = Settings.LeaderName.Value.ToLower();
			try
			{
				var nearest = GameController.IngameState.Data.ServerData.NearestPlayers;

				if (nearest != null)
				{
					foreach (var near in nearest)
					{
						if (near?.Owner?.GetComponent<Player>().PlayerName.ToLower() == leaderName)
						{
							return near?.Owner;
						}
					}
				}

				if (!GameController.EntityListWrapper.ValidEntitiesByType.TryGetValue(ExileCore.Shared.Enums.EntityType.Player, out ConcurrentBag<Entity> players))
				{
					return null;
				}

				return players.FirstOrDefault(x => x.GetComponent<Player>().PlayerName.ToLower() == leaderName);
			}
			// Sometimes we can get "Collection was modified; enumeration operation may not execute" exception
			catch
			{
				return null;
			}
		}

		private Entity GetLootableQuestItem()
		{
			try
			{
				if (!GameController.EntityListWrapper.ValidEntitiesByType.TryGetValue(ExileCore.Shared.Enums.EntityType.WorldItem, out ConcurrentBag<Entity> worldItems))
				{
					return null;
				}

				return worldItems
					.Where(e => e.IsTargetable)
					.Where(e => e.GetComponent<WorldItem>() != null)
					.FirstOrDefault(e =>
					{
						Entity itemEntity = e.GetComponent<WorldItem>().ItemEntity;
						return GameController.Files.BaseItemTypes.Translate(itemEntity.Path).ClassName ==
								"QuestItem";
					});
			}
			catch
			{
				return null;
			}
		}
		public override void EntityAdded(Entity entity)
		{
			bool defaultTransition = false;

			if (!string.IsNullOrEmpty(entity.RenderName))
			{
				switch (entity.Type)
				{
					//TODO: Handle doors and similar obstructions to movement/pathfinding

					//TODO: Handle waypoint (initial claim as well as using to teleport somewhere)

					//Handle clickable teleporters
					case ExileCore.Shared.Enums.EntityType.AreaTransition:
					case ExileCore.Shared.Enums.EntityType.Portal:
					case ExileCore.Shared.Enums.EntityType.TownPortal:
						defaultTransition = true;
						if (!_areaTransitions.ContainsKey(entity.Id))
							_areaTransitions.Add(entity.Id, entity);
						break;
				}
			}

			if (!defaultTransition && (entity.Metadata == "Metadata/Terrain/Leagues/Heist/Objects/MissionEntryPortal"
				|| entity.Metadata == "Metadata/Terrain/Leagues/Heist/Objects/MissionExitPortal"))
			{
				if(!_areaTransitions.ContainsKey(entity.Id))
					_areaTransitions.Add(entity.Id, entity);
			}

			base.EntityAdded(entity);
		}

		public override void EntityRemoved(Entity entity)
		{
			bool defaultTransition = false;

			switch (entity.Type)
			{
				case ExileCore.Shared.Enums.EntityType.AreaTransition:
				case ExileCore.Shared.Enums.EntityType.Portal:
				case ExileCore.Shared.Enums.EntityType.TownPortal:
					defaultTransition = true;

					if (_areaTransitions.ContainsKey(entity.Id))
						_areaTransitions.Remove(entity.Id);
					break;
			}

			
			if (!defaultTransition && (entity.Metadata == "Metadata/Terrain/Leagues/Heist/Objects/MissionEntryPortal"
				|| entity.Metadata == "Metadata/Terrain/Leagues/Heist/Objects/MissionExitPortal"))
			{
				if (!_areaTransitions.ContainsKey(entity.Id))
					_areaTransitions.Remove(entity.Id);
			}

			base.EntityRemoved(entity);
		}


		public override void Render()
		{
			Color followColor = Color.FromRgba(0x440000FF);
			Color keyColor = Color.FromRgba(0x440000FF);

			if (Settings.IsFollowEnabled.Value) 
			{
				followColor = Color.FromRgba(0x4400FF00);
			}

			if (Input.GetKeyState(Settings.MovementKey))
			{
				keyColor = Color.FromRgba(0x4400FF00);
			}

			var windowRect = GameController.Window.GetWindowRectangle();
			float width = 200;
			float height = 100;
			
			float x = windowRect.Center.X - windowRect.Left - width / 2;
			float y = windowRect.Center.Y - windowRect.Top;
			float yOffset = (windowRect.Bottom - windowRect.Top) / 4;
			float y1 = y - yOffset - height / 2;
			float y2 = y + yOffset - height / 2;

			if (Settings.IsDrawFollowMarkerEnabled)
			{
				Graphics.DrawBox(new RectangleF(x, y1, width, height), followColor, 15);
			}

			if (Settings.IsDrawKeyMarkerEnabled) 
			{
				Graphics.DrawBox(new RectangleF(x, y2, width, height), keyColor, 15);
			}
		}

		private Vector2 WorldToValidScreenPosition(Vector3 worldPos)
		{
			var windowRect = GameController.Window.GetWindowRectangle();
			var screenPos = Camera.WorldToScreen(worldPos);
			var result = screenPos + windowRect.Location;

			var edgeBounds = 250;
			if (!windowRect.Intersects(new SharpDX.RectangleF(result.X, result.Y, edgeBounds, edgeBounds)))
			{
				//Adjust for offscreen entity. Need to clamp the screen position using the game window info. 
				if (result.X < windowRect.TopLeft.X) result.X = windowRect.TopLeft.X + edgeBounds;
				if (result.Y < windowRect.TopLeft.Y) result.Y = windowRect.TopLeft.Y + edgeBounds;
				if (result.X > windowRect.BottomRight.X) result.X = windowRect.BottomRight.X - edgeBounds;
				if (result.Y > windowRect.BottomRight.Y) result.Y = windowRect.BottomRight.Y - edgeBounds;
			}
			return result;
		}

		private Vector2 WorldToValidScreenPosition(Vector3 worldPos, Vector3 offset)
		{
			var correctedPos = new Vector3(worldPos.X + offset.X, worldPos.Y + offset.Y, worldPos.Z + offset.Z);

			return WorldToValidScreenPosition(correctedPos);
		}
	}
}

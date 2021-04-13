﻿using System;
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
		private Entity _followTarget;

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
			_areaTransitions = new Dictionary<uint, Entity>();
			_hasUsedWP = false;
		}

		public override void AreaChange(AreaInstance area)
		{
			ResetPathing();

			//Load initial transitions!

			foreach (var transition in GameController.EntityListWrapper.Entities.Where(I => I.Type == ExileCore.Shared.Enums.EntityType.AreaTransition ||
			 I.Type == ExileCore.Shared.Enums.EntityType.Portal ||
			 I.Type == ExileCore.Shared.Enums.EntityType.TownPortal).ToList())
			{
				if(!_areaTransitions.ContainsKey(transition.Id))
					_areaTransitions.Add(transition.Id, transition);
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

			//Dont run logic if we're dead!
			if (!GameController.Player.IsAlive)
				return null;

			if (Settings.ToggleFollower.PressedOnce())
			{
				Settings.IsFollowEnabled.SetValueNoEvent(!Settings.IsFollowEnabled.Value);
				_tasks = new List<TaskNode>();				
			}

			if (!Settings.IsFollowEnabled.Value)
				return null;


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
						var transition = _areaTransitions.Values.OrderBy(I => Vector3.Distance(_lastTargetPosition, I.Pos)).FirstOrDefault();
						var dist = Vector3.Distance(_lastTargetPosition, transition.Pos);
						if (Vector3.Distance(_lastTargetPosition, transition.Pos) < Settings.ClearPathDistance.Value)
							_tasks.Add(new TaskNode(transition.Pos, 200, TaskNodeType.Transition));
					}
					//We have no path, set us to go to leader pos.
					else if (_tasks.Count == 0)
						_tasks.Add(new TaskNode(_followTarget.Pos, Settings.PathfindingNodeDistance));
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
						if (distanceFromFollower >= Settings.PathfindingNodeDistance.Value)
							_tasks.Add(new TaskNode(_followTarget.Pos, Settings.PathfindingNodeDistance));
					}

					//Check if we should add quest loot logic. We're close to leader already
					var questLoot = GetLootableQuestItem();
					if (questLoot != null &&
						Vector3.Distance(GameController.Player.Pos, questLoot.Pos) < Settings.ClearPathDistance.Value &&
						_tasks.FirstOrDefault(I => I.Type == TaskNodeType.Loot) == null)
						_tasks.Add(new TaskNode(questLoot.Pos, Settings.ClearPathDistance, TaskNodeType.Loot));

					else if (!_hasUsedWP)
					{
						//Check if there's a waypoint nearby
						var waypoint = GameController.EntityListWrapper.Entities.SingleOrDefault(I => I.Type == ExileCore.Shared.Enums.EntityType.Waypoint &&
							Vector3.Distance(GameController.Player.Pos, I.Pos) < Settings.ClearPathDistance);

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

				var transOptions = _areaTransitions.Values.
					Where(I => Vector3.Distance(_lastTargetPosition, I.Pos) < Settings.ClearPathDistance).
					OrderBy(I => Vector3.Distance(_lastTargetPosition, I.Pos)).ToArray();
				if (transOptions.Length > 0)
					_tasks.Add(new TaskNode(transOptions[random.Next(transOptions.Length)].Pos, Settings.PathfindingNodeDistance.Value, TaskNodeType.Transition));
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
						_nextBotAction = DateTime.Now.AddMilliseconds(Settings.BotInputFrequency + random.Next(Settings.BotInputFrequency));

						Mouse.SetCursorPosHuman2(WorldToValidScreenPosition(currentTask.WorldPosition));
						Thread.Sleep(random.Next(25) + 30);
						Input.KeyDown(Settings.MovementKey);
						Thread.Sleep(random.Next(25) + 30);
						Input.KeyUp(Settings.MovementKey);

						//Within bounding range. Task is complete
						//Note: Was getting stuck on close objects... testing hacky fix.
						if (taskDistance <= Settings.PathfindingNodeDistance.Value * 1.5)
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
							var screenPos = WorldToValidScreenPosition(currentTask.WorldPosition);							
							if (taskDistance <= Settings.ClearPathDistance.Value)
							{
								//Click the transition
								Input.KeyUp(Settings.MovementKey);
								Mouse.SetCursorPosAndLeftClickHuman(screenPos, 100);
								_nextBotAction = DateTime.Now.AddSeconds(1);
							}
							else
							{
								//Walk towards the transition
								Mouse.SetCursorPosHuman2(screenPos);
								Thread.Sleep(random.Next(25) + 30);
								Input.KeyDown(Settings.MovementKey);
								Thread.Sleep(random.Next(25) + 30);
								Input.KeyUp(Settings.MovementKey);
							}
							currentTask.AttemptCount++;
							if (currentTask.AttemptCount > 3)
								_tasks.RemoveAt(0);
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
								_tasks.RemoveAt(0);
							break;
						}
				}
			}
			_lastPlayerPosition = GameController.Player.Pos;
			return null;
		}
		

		private Entity GetFollowingTarget()
		{
			var leaderName = Settings.LeaderName.Value.ToLower();
			try
			{
				return GameController.Entities
					.Where(x => x.Type == ExileCore.Shared.Enums.EntityType.Player)
					.FirstOrDefault(x => x.GetComponent<Player>().PlayerName.ToLower() == leaderName);
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
				return GameController.EntityListWrapper.Entities
					.Where(e => e.Type == ExileCore.Shared.Enums.EntityType.WorldItem)
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
			if (!string.IsNullOrEmpty(entity.RenderName))
				switch (entity.Type)
				{
					//TODO: Handle doors and similar obstructions to movement/pathfinding

					//TODO: Handle waypoint (initial claim as well as using to teleport somewhere)

					//Handle clickable teleporters
					case ExileCore.Shared.Enums.EntityType.AreaTransition:
					case ExileCore.Shared.Enums.EntityType.Portal:
					case ExileCore.Shared.Enums.EntityType.TownPortal:
						if (!_areaTransitions.ContainsKey(entity.Id))
							_areaTransitions.Add(entity.Id, entity);
						break;
				}
			base.EntityAdded(entity);
		}

		public override void EntityRemoved(Entity entity)
		{
			switch (entity.Type)
			{
				//TODO: Handle doors and similar obstructions to movement/pathfinding

				//TODO: Handle waypoint (initial claim as well as using to teleport somewhere)

				//Handle clickable teleporters
				case ExileCore.Shared.Enums.EntityType.AreaTransition:
				case ExileCore.Shared.Enums.EntityType.Portal:
				case ExileCore.Shared.Enums.EntityType.TownPortal:
					if (_areaTransitions.ContainsKey(entity.Id))
						_areaTransitions.Remove(entity.Id);
					break;
			}
			base.EntityRemoved(entity);
		}


		public override void Render()
		{
			Color followColor = Color.FromRgba(0x880000FF);
			Color keyColor = Color.FromRgba(0x880000FF);

			if (Settings.IsFollowEnabled.Value) 
			{
				followColor = Color.FromRgba(0x8800FF00);
			}

			if (Input.GetKeyState(Settings.MovementKey))
			{
				keyColor = Color.FromRgba(0x8800FF00);
			}

			var windowRect = GameController.Window.GetWindowRectangle();
			float width = 200;
			float height = 100;
			
			float x = windowRect.Center.X - windowRect.Left - width / 2;
			float y = windowRect.Center.Y - windowRect.Top;
			float yOffset = (windowRect.Bottom - windowRect.Top) / 4;
			float y1 = y - yOffset - height / 2;
			float y2 = y + yOffset - height / 2;

			Graphics.DrawBox(new RectangleF(x, y1, width, height), followColor, 15);
			Graphics.DrawBox(new RectangleF(x, y2, width, height), keyColor, 15);
		}


		private Vector2 WorldToValidScreenPosition(Vector3 worldPos)
		{
			var windowRect = GameController.Window.GetWindowRectangle();
			var screenPos = Camera.WorldToScreen(worldPos);
			var result = screenPos + windowRect.Location;

			var edgeBounds = 50;
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
	}
}
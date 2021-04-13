using System.Windows.Forms;
using ExileCore.Shared.Attributes;
using ExileCore.Shared.Interfaces;
using ExileCore.Shared.Nodes;
using System.IO;

namespace SimpleFllw
{
	class SimpleFllwSettings : ISettings
	{
		public ToggleNode Enable { get; set; } = new ToggleNode(false);
		public ToggleNode IsFollowEnabled { get; set; } = new ToggleNode(false);
		[Menu("Toggle Follower")] public HotkeyNode ToggleFollower { get; set; } = Keys.PageUp;
		[Menu("Min Path Distance")] public RangeNode<int> PathfindingNodeDistance { get; set; } = new RangeNode<int>(161, 10, 1000);
		[Menu("Move CMD Frequency")]public RangeNode<int> BotInputFrequency { get; set; } = new RangeNode<int>(50, 10, 250);
		[Menu("Stop Path Distance")] public RangeNode<int> ClearPathDistance { get; set; } = new RangeNode<int>(740, 100, 5000);
		[Menu("Follow Offset Direction")] public RangeNode<int> FollowOffsetDirection { get; set; } = new RangeNode<int>(90, 0, 200);
		[Menu("Follow Offset Normal")] public RangeNode<int> FollowOffsetNormal { get; set; } = new RangeNode<int>(60, -150, 150);
		[Menu("Follow Target Name")] public TextNode LeaderName { get; set; } = new TextNode("");
		[Menu("Movement Key")] public HotkeyNode MovementKey { get; set; } = Keys.T;
		[Menu("Allow Dash")] public ToggleNode IsDashEnabled { get; set; } = new ToggleNode(true);
		[Menu("Dash Key")] public HotkeyNode DashKey { get; set; } = Keys.W;
		[Menu("Loot Key")] public HotkeyNode LootKey { get; set; } = Keys.F;
		[Menu("Follow Close")] public ToggleNode IsCloseFollowEnabled { get; set; } = new ToggleNode(false);

	}
}
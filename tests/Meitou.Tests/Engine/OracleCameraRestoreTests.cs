using System.Numerics;
using Meitou.Game.Host.Cameras;
using Meitou.Game.Host.Input;

namespace Meitou.Tests.Engine;

public class OracleCameraRestoreTests
{
    [Fact]
    public void Restore_cuts_to_the_exact_captured_pivot_height_and_discards_pending_turn_input()
    {
        var rig = new CameraRig((_, _) => 0);
        var input = new InputState();
        var bindings = new InputBindings();
        rig.Place(new Vector2(20, 30), .7f, .4f, 150);
        var original = rig.Current;
        input.SetKey(Key.Q, true);
        rig.Update(1 / 30f, bindings.Resolve(input.Consume()));
        input.SetKey(Key.Q, false);
        // The nonzero Y is deliberately above the settled terrain goal. Place would discard it.
        var pivot = original.Target + new Vector3(0, 37, 0);
        rig.RestorePose(pivot, original.Yaw, original.Pitch, original.Distance, false);
        Assert.Equal(pivot, rig.Current.Target);
        Assert.Equal(rig.Current, rig.Previous);
        Assert.Equal(rig.Current, rig.At(.5f));
        rig.Update(1 / 30f, bindings.Resolve(input.Consume()));
        Assert.Equal(original.Yaw, rig.Current.Yaw, 6);
        Assert.Equal(original.Pitch, rig.Current.Pitch, 6);
        Assert.True(rig.Current.Target.Y < pivot.Y); // normal clearance/height following resumes
    }

    [Fact]
    public void Free_restore_keeps_the_captured_eye_and_boom_and_starts_at_rest()
    {
        var rig = new CameraRig((_, _) => 0);
        var target = new Vector3(50, 100, 80);
        rig.RestorePose(target, .3f, .6f, 300, true);
        Assert.True(rig.IsFree);
        Assert.Equal(Vector3.Zero, rig.Free.Velocity);
        Assert.InRange(Vector3.Distance(CameraState.EyeFor(target, .3f, .6f, 300), rig.Current.Eye), 0, .0001f);
        Assert.InRange(Vector3.Distance(target, rig.Current.Target), 0, .0001f);
        Assert.Equal(300, rig.Current.Distance);
    }

    [Fact]
    public void Invalid_restore_does_not_change_either_camera_or_the_presented_pair()
    {
        var rig = new CameraRig((_, _) => 0);
        var before = rig.Current;
        Assert.Throws<ArgumentOutOfRangeException>(() => rig.RestorePose(new Vector3(float.NaN), 0, 0, 150, true));
        Assert.Equal(before, rig.Current);
        Assert.False(rig.IsFree);
    }
}

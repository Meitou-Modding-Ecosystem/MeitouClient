using System.Numerics;
using Meitou.Game.Title;
using Xunit;

namespace Meitou.Tests.Game;

/// <summary>
/// The wanderer's flight from the title's Beak Thing (<see cref="TitleFlight"/>): a short startled step back still facing out, a small walking turn
/// round to the camera, then a run at it.
/// </summary>
public class TitleFlightTests
{
    /// <summary>
    /// A wanderer facing +Z (yaw 0) with the camera behind him, up and to the side a little.
    /// </summary>
    static readonly TitleFlight Flight = new(new Vector3(100, 50, 100), 0, new Vector3(130, 400, -200), FlightClips.None);

    /// <summary>
    /// The way back to the camera, across the ground.
    /// </summary>
    static Vector2 Back => Vector2.Normalize(new Vector2(30, -300));

    /// <summary>
    /// The stagger carries him back towards the camera by at most its distance, still facing out.
    /// </summary>
    [Fact]
    public void Stagger_is_a_short_step_back_facing_out()
    {
        var (at, yaw, _) = Flight.At(TitleFlight.StaggerSeconds - 0.001f);
        var moved = new Vector2(at.X - 100, at.Z - 100);
        Assert.InRange(moved.Length(), TitleFlight.StaggerDistance * 0.95f, TitleFlight.StaggerDistance);
        Assert.True(Vector2.Dot(Vector2.Normalize(moved), Back) > 0.999f);
        Assert.Equal(0, yaw, 3);
        Assert.Equal(50, at.Y);
    }

    /// <summary>
    /// The turn stays within its small circle and ends facing the camera, where the run sets off, without a jump.
    /// </summary>
    [Fact]
    public void Turn_is_a_small_circle_ending_towards_the_camera()
    {
        var staggered = new Vector3(100, 50, 100) + new Vector3(Back.X, 0, Back.Y) * TitleFlight.StaggerDistance;
        for (float t = 0; t < TitleFlight.TurnSeconds; t += 0.05f)
        {
            var (at, _, _) = Flight.At(TitleFlight.StaggerSeconds + t);
            Assert.True(Vector3.Distance(at, staggered) <= 2 * TitleFlight.TurnRadius + 1e-3f, $"at {t}: {at}");
        }

        var (end, yaw, _) = Flight.At(TitleFlight.StaggerSeconds + TitleFlight.TurnSeconds - 0.001f);
        var (start, runYaw, _) = Flight.At(TitleFlight.StaggerSeconds + TitleFlight.TurnSeconds);
        Assert.True(Vector3.Distance(end, start) < 0.2f);
        Assert.InRange(MathF.Abs(MathF.Atan2(Back.X, Back.Y) - yaw), 0, 0.01f);
        Assert.InRange(MathF.Abs(runYaw - yaw), 0, 0.1f);
    }

    /// <summary>
    /// The run heads at the camera at its speed.
    /// </summary>
    [Fact]
    public void Run_heads_at_the_camera()
    {
        float off = TitleFlight.StaggerSeconds + TitleFlight.TurnSeconds;
        var (a, _, _) = Flight.At(off + 1);
        var (b, _, _) = Flight.At(off + 2);
        var step = new Vector2(b.X - a.X, b.Z - a.Z);
        Assert.Equal(TitleFlight.RunSpeed, step.Length(), 1);
        Assert.True(Vector2.Dot(Vector2.Normalize(step), Vector2.Normalize(new Vector2(130 - a.X, -200 - a.Z))) > 0.999f);
    }
}

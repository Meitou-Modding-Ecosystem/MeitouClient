using System.Numerics;
using Meitou.Game.Rendering.Frame;

namespace Meitou.Game.Title;

/// <summary>
/// One skeleton animation of the wanderer's flight: its name, its rate (clip seconds per world unit moved, a move clip's play speed; for a clip that
/// is not a move, clip seconds per second) and its length in seconds.
/// </summary>
readonly record struct FlightClip(string Clip, float Rate, float Length)
{
    /// <summary>
    /// The pose of a move clip, looping, after moving <paramref name="moved"/> world units.
    /// </summary>
    public CharacterPose Looped(float moved) => new(Clip, Length > 0 ? moved * Rate % Length : 0, 1);

    /// <summary>
    /// The pose <paramref name="seconds"/> into a clip played once, held on its last frame.
    /// </summary>
    public CharacterPose Once(float seconds) => new(Clip, MathF.Min(seconds * Rate, MathF.Max(Length - 0.01f, 0)), 1);
}

/// <summary>
/// The skeleton animations of the wanderer's flight from the title's Beak Thing (<see cref="TitleFlight"/>): the start, the walk of the turn and the
/// run, each in parts (legs and arms) or none.
/// </summary>
sealed record FlightClips(IReadOnlyList<FlightClip> Startle, IReadOnlyList<FlightClip> Walk, IReadOnlyList<FlightClip> Run)
{
    /// <summary>
    /// No clips: the wanderer keeps his idle and only moves.
    /// </summary>
    public static FlightClips None { get; } = new([], [], []);
}

/// <summary>
/// The wanderer's flight from the title's Beak Thing (<see cref="TitleAmbush"/>), display only: as it turns for the hill he staggers back a step,
/// startled, still facing it; then turns round in a small walking circle, so his turn shows; then runs at the camera and out of the frame.
/// </summary>
/// <param name="Start">Where he stands.</param>
/// <param name="Yaw">His facing as he watches (out over the valley; the characters' yaw: 0 faces +Z).</param>
/// <param name="Eye">The camera's eye: he staggers towards it and runs under it.</param>
/// <param name="Clips">His clips.</param>
sealed record TitleFlight(Vector3 Start, float Yaw, Vector3 Eye, FlightClips Clips)
{
    /// <summary>
    /// The startled stagger: how long it lasts and how far back it carries him (world units; the camera is close behind him, so a short step keeps
    /// him in the frame).
    /// </summary>
    public const float StaggerSeconds = 1.1f, StaggerDistance = 7;

    /// <summary>
    /// The turn: how long the walking circle lasts and its radius (world units).
    /// </summary>
    public const float TurnSeconds = 1.3f, TurnRadius = 5;

    /// <summary>
    /// His running speed (world units/s).
    /// </summary>
    public const float RunSpeed = 70;

    /// <summary>
    /// The longest he is followed: by then he is far out of the frame.
    /// </summary>
    const float LastSeconds = 30;

    /// <summary>
    /// The idle he watches in, and holds without clips.
    /// </summary>
    static readonly CharacterPose Watching = new("idle_stand_confident", 0.5f, 1);

    /// <summary>
    /// The way back from where he stands: towards the camera, across the ground.
    /// </summary>
    Vector2 Back => Vector2.Normalize(new Vector2(Eye.X - Start.X, Eye.Z - Start.Z) + new Vector2(1e-4f, 0));

    /// <summary>
    /// The turn's whole angle: from his watching facing round to the way back (signed: positive turns to increasing yaw).
    /// </summary>
    float TurnAngle => MathF.IEEERemainder(MathF.Atan2(Back.X, Back.Y) - Yaw, MathF.Tau);

    /// <summary>
    /// Where he is, his facing and his poses <paramref name="seconds"/> into the flight.
    /// </summary>
    public (Vector3 At, float Yaw, CharacterPose[] Poses) At(float seconds)
    {
        seconds = Math.Clamp(seconds, 0, LastSeconds);
        if (seconds < StaggerSeconds) return Stagger(seconds);
        if (seconds < StaggerSeconds + TurnSeconds) return Turn(seconds - StaggerSeconds);

        return Run(seconds - StaggerSeconds - TurnSeconds);
    }

    /// <summary>
    /// The stagger <paramref name="seconds"/> in: still facing out, carried back fast then slowing, playing the start clip once.
    /// </summary>
    (Vector3, float, CharacterPose[]) Stagger(float seconds)
    {
        float u = seconds / StaggerSeconds;
        float back = StaggerDistance * (1 - (1 - u) * (1 - u));
        var poses = Clips.Startle.Count > 0 ? Clips.Startle.Select(c => c.Once(seconds)).ToArray() : [Watching];

        return (Start + new Vector3(Back.X, 0, Back.Y) * back, Yaw, poses);
    }

    /// <summary>
    /// Where the stagger left him.
    /// </summary>
    Vector3 Staggered => Start + new Vector3(Back.X, 0, Back.Y) * StaggerDistance;

    /// <summary>
    /// The turn <paramref name="seconds"/> in: walking forward round a circle of <see cref="TurnRadius"/>, his facing along it, from his watching
    /// facing to the way back.
    /// </summary>
    (Vector3, float, CharacterPose[]) Turn(float seconds)
    {
        float turned = TurnAngle * (seconds / TurnSeconds);
        var (at, yaw) = OnCircle(turned);
        float walked = MathF.Abs(turned) * TurnRadius;
        var poses = Clips.Walk.Count > 0 ? Clips.Walk.Select(c => c.Looped(walked)).ToArray() : [Watching];

        return (at, yaw, poses);
    }

    /// <summary>
    /// The place and facing after turning <paramref name="turned"/> round the circle: moving along the facing <c>(sin yaw, cos yaw)</c> while it turns.
    /// </summary>
    (Vector3 At, float Yaw) OnCircle(float turned)
    {
        float s = MathF.Sign(turned) is 0 ? 1 : MathF.Sign(turned);
        float yaw = Yaw + turned;
        float x = TurnRadius * s * (MathF.Cos(Yaw) - MathF.Cos(yaw));
        float z = TurnRadius * s * (MathF.Sin(yaw) - MathF.Sin(Yaw));

        return (Staggered + new Vector3(x, 0, z), yaw);
    }

    /// <summary>
    /// The run <paramref name="seconds"/> in: from the end of the circle, straight at the camera.
    /// </summary>
    (Vector3, float, CharacterPose[]) Run(float seconds)
    {
        var (from, _) = OnCircle(TurnAngle);
        var dir = Vector2.Normalize(new Vector2(Eye.X - from.X, Eye.Z - from.Z) + new Vector2(1e-4f, 0));
        float run = RunSpeed * seconds;
        var poses = Clips.Run.Count > 0 ? Clips.Run.Select(c => c.Looped(run)).ToArray() : [Watching];

        return (from + new Vector3(dir.X, 0, dir.Y) * run, MathF.Atan2(dir.X, dir.Y), poses);
    }
}

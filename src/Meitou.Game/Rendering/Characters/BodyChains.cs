using System.Numerics;
using Meitou.Data.Characters;

namespace Meitou.Game.Rendering.Characters;

/// <summary>
/// Spring chains on a body's own bones (the <c>sway</c> switch, with <see cref="GearSway"/>): a tail, a braid or a bundle of cables rigged as a chain
/// of bones named <see cref="Prefix"/>… swings behind the body's motion instead of following the bone it hangs from rigidly
/// (docs/character-renderer.md "Gear sway", body chains). The skeleton holds the chain's bones after the body's (a body mesh may link a skeleton
/// of its own that links the body's clips, as the 45-bone finger skeletons do); no clip need move them. Drawing only: the simulation never sees it.
/// </summary>
static class BodyChains
{
    #region Rig

    /// <summary>
    /// The start of the name of every bone of a chain, e.g. <c>Sway_Tail_01</c>; the case does not matter.
    /// </summary>
    public const string Prefix = "Sway_";

    /// <summary>
    /// One link of a chain: <see cref="Bone"/> turns so that its child <see cref="Child"/> (the next bone of the chain) follows the swing.
    /// <see cref="Up"/> is the link before it in the same chain (an index into the asset's links), -1 for the first link, which hangs from
    /// <see cref="Anchor"/>, the body bone the chain is on. <see cref="Length"/> is from the bone to its child in the binding pose.
    /// </summary>
    public readonly record struct Link(int Bone, int Child, int Up, int Anchor, float Length);

    /// <summary>
    /// The links of <paramref name="rig"/>'s chains, parents first, and for every handle the link whose swing it follows (-1: none): a link's own
    /// bone, and a chain's last bone, its tip, which has no child to swing towards. A chain bone with several chain children follows the first.
    /// </summary>
    public static (Link[] Links, int[] Follows) Find(SkeletonRig rig)
    {
        var links = new List<Link>();
        var follows = new int[rig.BoneCount];
        Array.Fill(follows, -1);
        foreach (int bone in rig.Order)
        {
            if (!IsChainBone(rig, bone)) continue;
            int parent = rig.Parent[bone];
            bool first = parent < 0 || !IsChainBone(rig, parent);
            if (first && parent < 0) continue;   // a chain needs a body bone to hang from

            int up = first ? -1 : follows[parent];
            if (FirstChainChild(rig, bone) is int child)
            {
                follows[bone] = links.Count;
                int anchor = first ? parent : links[up].Anchor;
                float length = Vector3.Distance(rig.BindPosition[bone], rig.BindPosition[child]);
                links.Add(new Link(bone, child, up, anchor, length));
            }
            else
            {
                follows[bone] = up;
            }
        }
        return (links.Where(l => l.Length > 1e-4f).ToArray(), follows);
    }

    /// <summary>
    /// The bone's name marks it as a chain bone.
    /// </summary>
    static bool IsChainBone(SkeletonRig rig, int bone) =>
        rig.ByHandle[bone]?.Name.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase) == true;

    /// <summary>
    /// The lowest-handled child of <paramref name="bone"/> that is a chain bone; null when it has none (the chain's tip).
    /// </summary>
    static int? FirstChainChild(SkeletonRig rig, int bone)
    {
        for (int h = 0; h < rig.BoneCount; h++)
            if (rig.Parent[h] == bone && IsChainBone(rig, h)) return h;
        return null;
    }

    #endregion

    #region Swing

    /// <summary>
    /// How a chain swings: each link's tip is pulled toward where the pose and the link above hold it (per second squared) and damped against that
    /// place's own speed (per second), turns no farther than <see cref="MaxAngle"/> (radians) from it, and is kept out of the wearer's hips and legs
    /// by <see cref="Thickness"/> (units).
    /// </summary>
    public readonly record struct Spring(float Stiffness, float Damping, float MaxAngle, float Thickness);

    /// <summary>
    /// The chains' spring: 1.8 Hz per link, damping ratio 0.35, 40 degrees at most from the posed shape.
    /// </summary>
    public static readonly Spring Default = new(128f, 8f, 0.698f, 0.25f);

    /// <summary>
    /// The longest integration step (seconds), as <see cref="GearSway"/>'s.
    /// </summary>
    const float Step = 1f / 120;

    /// <summary>
    /// A gap longer than this (seconds of game time) starts the chains at rest.
    /// </summary>
    const float LongGap = 0.25f;

    /// <summary>
    /// A first link's anchor that moved farther than this many link lengths in one update has been placed: the chain starts at rest.
    /// </summary>
    const float JumpLinks = 6;

    /// <summary>
    /// One link's swing in the world: where its tip is and how fast it moves, and where the pose held the link at the last update.
    /// </summary>
    public struct State
    {
        /// <summary>
        /// The tip's place and speed.
        /// </summary>
        public Vector3 Tip, Velocity;

        /// <summary>
        /// The posed head and the posed direction from it to the tip at the last update.
        /// </summary>
        public Vector3 Head, Way;

        /// <summary>
        /// The state holds a swing.
        /// </summary>
        public bool Valid;
    }

    /// <summary>
    /// Steps every link from the last update to this one (<paramref name="dt"/> seconds of game time; negative: start at rest) and turns the chains'
    /// bones in <paramref name="skin"/> (the palette, mesh to model space) by their swing.
    /// </summary>
    /// <param name="bind">The rig's binding-pose bone places by handle (<see cref="SkeletonRig.BindPosition"/>).</param>
    /// <param name="world">The character's model-to-world matrix.</param>
    /// <param name="limbs">The wearer's limbs the chains are kept out of (<see cref="SwayCollider"/>, mesh space on their bones).</param>
    public static void Update(ReadOnlySpan<Link> links, ReadOnlySpan<int> follows, Span<State> states, Span<Matrix4x4> skin,
        ReadOnlySpan<Vector3> bind, in Matrix4x4 world, ReadOnlySpan<SwayCollider> limbs, float dt, in Spring spring)
    {
        if (links.Length == 0 || !Matrix4x4.Invert(world, out var toModel)) return;

        Span<Vector3> head = stackalloc Vector3[links.Length], way = stackalloc Vector3[links.Length];
        Posed(links, skin, bind, world, head, way);
        bool restart = !(dt >= 0) || dt > LongGap || Placed(links, states, head);
        if (restart) Rest(links, states, head, way);
        else if (dt > 0) Integrate(links, states, skin, world, limbs, head, way, dt, spring);

        Turn(links, follows, states, skin, toModel, head, way);
    }

    /// <summary>
    /// Each link's posed head and unit direction to its tip, in the world: the child as its bone holds it, so the chain's own clip tracks count.
    /// </summary>
    static void Posed(ReadOnlySpan<Link> links, ReadOnlySpan<Matrix4x4> skin, ReadOnlySpan<Vector3> bind, in Matrix4x4 world, Span<Vector3> head,
        Span<Vector3> way)
    {
        for (int i = 0; i < links.Length; i++)
        {
            var link = links[i];
            var m = skin[link.Bone] * world;
            head[i] = Vector3.Transform(bind[link.Bone], m);
            var d = Vector3.Transform(bind[link.Child], m) - head[i];
            way[i] = d.LengthSquared() > 1e-12f ? Vector3.Normalize(d) : Vector3.UnitY;
        }
    }

    /// <summary>
    /// A first link's anchor jumped (a teleport, a carry, a new look): the chain is placed, not moved.
    /// </summary>
    static bool Placed(ReadOnlySpan<Link> links, ReadOnlySpan<State> states, ReadOnlySpan<Vector3> head)
    {
        for (int i = 0; i < links.Length; i++)
        {
            if (!states[i].Valid) return true;
            if (links[i].Up < 0 && Vector3.Distance(head[i], states[i].Head) > JumpLinks * links[i].Length) return true;
        }
        return false;
    }

    /// <summary>
    /// Every link as posed, at rest.
    /// </summary>
    static void Rest(ReadOnlySpan<Link> links, Span<State> states, ReadOnlySpan<Vector3> head, ReadOnlySpan<Vector3> way)
    {
        for (int i = 0; i < links.Length; i++)
            states[i] = new State { Tip = head[i] + way[i] * links[i].Length, Head = head[i], Way = way[i], Valid = true };
    }

    /// <summary>
    /// Steps the links at <see cref="Step"/> or finer, the posed heads and directions moving evenly from the last update's to this one's.
    /// </summary>
    static void Integrate(ReadOnlySpan<Link> links, Span<State> states, ReadOnlySpan<Matrix4x4> skin, in Matrix4x4 world,
        ReadOnlySpan<SwayCollider> limbs, ReadOnlySpan<Vector3> head, ReadOnlySpan<Vector3> way, float dt, in Spring spring)
    {
        int steps = Math.Max(1, (int)MathF.Ceiling(dt / Step));
        float h = dt / steps;
        Span<Vector3> restSpeed = stackalloc Vector3[links.Length], swung = stackalloc Vector3[links.Length];
        for (int i = 0; i < links.Length; i++)
            restSpeed[i] = (head[i] + way[i] * links[i].Length - states[i].Head - states[i].Way * links[i].Length) / dt;

        for (int k = 1; k <= steps; k++)
        {
            float f = (float)k / steps;
            for (int i = 0; i < links.Length; i++)
            {
                var link = links[i];
                var posedHead = Vector3.Lerp(states[i].Head, head[i], f);
                var posedWay = Slerp(states[i].Way, way[i], f);
                var from = link.Up < 0 ? posedHead : states[link.Up].Tip;
                var target = link.Up < 0 ? posedWay : Vector3.Transform(posedWay, UpSwing(states, way, swung, link.Up, f));
                StepLink(ref states[i], from, target, link.Length, restSpeed[i], h, spring);
                PushOut(ref states[i], from, link, skin, world, limbs, spring.Thickness);
                swung[i] = Vector3.Normalize(states[i].Tip - from);
            }
        }
        for (int i = 0; i < links.Length; i++) (states[i].Head, states[i].Way) = (head[i], way[i]);
    }

    /// <summary>
    /// How far link <paramref name="up"/> has swung from its posed direction at the step's fraction <paramref name="f"/> of the update.
    /// </summary>
    static Quaternion UpSwing(ReadOnlySpan<State> states, ReadOnlySpan<Vector3> way, ReadOnlySpan<Vector3> swung, int up, float f) =>
        FromTo(Slerp(states[up].Way, way[up], f), swung[up]);

    /// <summary>
    /// One step of one link: a damped spring toward the place the link above and the pose give it, then back on the sphere of its length round its
    /// head and within the spring's angle of that place.
    /// </summary>
    static void StepLink(ref State s, Vector3 from, Vector3 target, float length, Vector3 restSpeed, float h, in Spring spring)
    {
        var goal = from + target * length;
        s.Velocity += (spring.Stiffness * (goal - s.Tip) + spring.Damping * (restSpeed - s.Velocity)) * h;
        s.Tip += s.Velocity * h;

        var dir = s.Tip - from;
        dir = dir.LengthSquared() > 1e-10f ? Vector3.Normalize(dir) : target;
        float cos = Vector3.Dot(dir, target), limit = MathF.Cos(spring.MaxAngle);
        if (cos < limit)
        {
            var side = dir - target * cos;
            side = side.LengthSquared() > 1e-10f ? Vector3.Normalize(side) : Vector3.Zero;
            dir = target * limit + side * MathF.Sin(spring.MaxAngle);
            s.Velocity = restSpeed + (s.Velocity - restSpeed) * 0.5f;   // a soft stop, no bounce off the limit
        }
        s.Tip = from + dir * length;
        s.Velocity -= dir * Vector3.Dot(s.Velocity - restSpeed, dir);   // no speed along the link
    }

    /// <summary>
    /// How many times a link's tip is pushed out of one limb per step: each push moves it across the link and then back onto the link's length.
    /// </summary>
    const int Pushes = 3;

    /// <summary>
    /// Moves a link's tip out of the wearer's limbs (each limb's capsule grown by <paramref name="thickness"/>) by turning the link about its head.
    /// The limb the chain hangs from does not count: the chain starts in it.
    /// </summary>
    static void PushOut(ref State s, Vector3 from, in Link link, ReadOnlySpan<Matrix4x4> skin, in Matrix4x4 world, ReadOnlySpan<SwayCollider> limbs,
        float thickness)
    {
        foreach (ref readonly var limb in limbs)
        {
            if (limb.Bone == link.Anchor || (uint)limb.Bone >= (uint)skin.Length) continue;

            var m = skin[limb.Bone] * world;
            float reach = limb.Radius * Vector3.TransformNormal(Vector3.UnitX, m).Length() + thickness;
            Vector3 a = Vector3.Transform(limb.From, m), b = Vector3.Transform(limb.To, m);
            for (int k = 0; k < Pushes; k++)
                if (!PushOnce(ref s, from, link.Length, a, b, reach)) break;
        }
    }

    /// <summary>
    /// One push of the tip out of the capsule <paramref name="a"/>–<paramref name="b"/> of radius <paramref name="reach"/>, across the link, and
    /// its speed into the capsule taken away; false when it is clear.
    /// </summary>
    static bool PushOnce(ref State s, Vector3 from, float length, Vector3 a, Vector3 b, float reach)
    {
        var off = s.Tip - Nearest(a, b, s.Tip);
        float d = off.Length();
        if (d >= reach) return false;

        var way = s.Tip - from;
        if (way.LengthSquared() < 1e-10f) return false;
        way = Vector3.Normalize(way);
        var push = off - way * Vector3.Dot(off, way);
        if (push.LengthSquared() < 1e-10f) push = Vector3.Cross(b - a, way);
        if (push.LengthSquared() < 1e-10f) return false;
        push = Vector3.Normalize(push);

        s.Tip = from + Vector3.Normalize(s.Tip + push * (reach - d) - from) * length;
        float into = Vector3.Dot(s.Velocity, push);
        if (into < 0) s.Velocity -= push * into;
        return true;
    }

    /// <summary>
    /// The point of segment <paramref name="a"/>–<paramref name="b"/> nearest <paramref name="p"/>.
    /// </summary>
    static Vector3 Nearest(Vector3 a, Vector3 b, Vector3 p)
    {
        var ab = b - a;
        float l2 = ab.LengthSquared();
        float t = l2 < 1e-10f ? 0 : Math.Clamp(Vector3.Dot(p - a, ab) / l2, 0, 1);
        return a + ab * t;
    }

    /// <summary>
    /// Writes the swing into the palette: each link's bone turned about its posed head onto its swung head and tip, and every bone that follows a
    /// link (its tip) by the same move. Model space: the world only places the character.
    /// </summary>
    static void Turn(ReadOnlySpan<Link> links, ReadOnlySpan<int> follows, ReadOnlySpan<State> states, Span<Matrix4x4> skin, in Matrix4x4 toModel,
        ReadOnlySpan<Vector3> head, ReadOnlySpan<Vector3> way)
    {
        Span<Matrix4x4> move = stackalloc Matrix4x4[links.Length];
        for (int i = 0; i < links.Length; i++)
        {
            var link = links[i];
            var posedHead = Vector3.Transform(head[i], toModel);
            var posedWay = Vector3.TransformNormal(way[i], toModel);
            var swungHead = Vector3.Transform(link.Up < 0 ? head[i] : states[link.Up].Tip, toModel);
            var swungWay = Vector3.Transform(states[i].Tip, toModel) - swungHead;
            if (posedWay.LengthSquared() < 1e-12f || swungWay.LengthSquared() < 1e-12f)
            {
                move[i] = Matrix4x4.Identity;
                continue;
            }
            var turn = FromTo(Vector3.Normalize(posedWay), Vector3.Normalize(swungWay));
            move[i] = Matrix4x4.CreateTranslation(-posedHead) * Matrix4x4.CreateFromQuaternion(turn) * Matrix4x4.CreateTranslation(swungHead);
        }
        for (int b = 0; b < follows.Length && b < skin.Length; b++)
            if (follows[b] >= 0) skin[b] *= move[follows[b]];
    }

    /// <summary>
    /// The unit direction a fraction <paramref name="f"/> of the way from unit <paramref name="a"/> to unit <paramref name="b"/>.
    /// </summary>
    static Vector3 Slerp(Vector3 a, Vector3 b, float f)
    {
        var q = Quaternion.Slerp(Quaternion.Identity, FromTo(a, b), f);
        return Vector3.Transform(a, q);
    }

    /// <summary>
    /// The shortest rotation taking unit vector <paramref name="a"/> to unit vector <paramref name="b"/> (identity when they are opposite).
    /// </summary>
    static Quaternion FromTo(Vector3 a, Vector3 b)
    {
        float d = Vector3.Dot(a, b);
        if (d > 0.999999f) return Quaternion.Identity;
        var axis = Vector3.Cross(a, b);
        if (axis.LengthSquared() < 1e-12f) return Quaternion.Identity;
        return Quaternion.Normalize(new Quaternion(axis, 1 + d));
    }

    #endregion
}

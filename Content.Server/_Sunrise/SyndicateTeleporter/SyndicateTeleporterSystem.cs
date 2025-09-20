using System.Numerics;
using Content.Server.Popups;
using Content.Shared._Sunrise.Biocode;
using Content.Shared._Sunrise.SyndicateTeleporter;
using Content.Shared.Charges.Components;
using Content.Shared.Charges.Systems;
using Content.Shared.Damage;
using Content.Shared.Interaction.Events;
using Content.Shared.Maps;
using Content.Shared.Physics;
using Robust.Shared.Map;
using Robust.Shared.Maths;
using Robust.Shared.Physics.Systems;
using Robust.Shared.Random;

namespace Content.Server._Sunrise.SyndicateTeleporter;

public sealed class SyndicateTeleporterSystem : EntitySystem
{
    [Dependency] private readonly SharedTransformSystem _transform = default!;
    [Dependency] private readonly SharedChargesSystem _charges = default!;
    [Dependency] private readonly TurfSystem _turf = default!;
    [Dependency] private readonly DamageableSystem _damage = default!;
    [Dependency] private readonly IRobustRandom _random = default!;
    [Dependency] private readonly PopupSystem _popup = default!;
    [Dependency] private readonly BiocodeSystem _biocode = default!;
    [Dependency] private readonly SharedPhysicsSystem _physics = default!;

    private const string SourceEffectPrototype = "TeleportEffectSource";
    private const string TargetEffectPrototype = "TeleportEffectTarget";

    private const int MaxCorrectionTries = 16;
    private const int MaxCorrectionRadius = 4;

    public override void Initialize()
    {
        base.Initialize();
        SubscribeLocalEvent<SyndicateTeleporterComponent, UseInHandEvent>(OnUse);
    }

    private void OnUse(EntityUid uid, SyndicateTeleporterComponent comp, UseInHandEvent args)
    {
        if (args.Handled)
            return;

        if (TryComp<BiocodeComponent>(uid, out var biocode) && !_biocode.CanUse(args.User, biocode.Factions))
        {
            if (!string.IsNullOrEmpty(biocode.AlertText))
                _popup.PopupEntity(biocode.AlertText, args.User, args.User);
            args.Handled = true;
            return;
        }

        if (!TryComp<LimitedChargesComponent>(uid, out var charges))
            return;
        if (_charges.IsEmpty((uid, charges)))
            return;

        _charges.TryUseCharge((uid, charges));
        Teleport(uid, args.User, comp);
        args.Handled = true;
    }

    private void Teleport(EntityUid device, EntityUid user, SyndicateTeleporterComponent comp)
    {
        var pre = Transform(user).Coordinates;

        var random = comp.RandomDistanceValue > 0 ? _random.Next(0, comp.RandomDistanceValue + 1) : 0;
        var dist = comp.TeleportationValue + random;
        var dir = Transform(user).LocalRotation.ToWorldVec().Normalized();
        var target = pre.Offset(dir * new Vector2(dist, dist));

        Spawn(SourceEffectPrototype, _transform.ToMapCoordinates(pre));

        if (Transform(user).MapID != target.GetMapId(EntityManager))
            return;

        // Свободно - нет урона
        if (IsSpotFree(user, target))
        {
            ApplyLanding(user, target);
            return;
        }

        // Стенка - урон
        if (TryFindSafeTile(user, target, out var safe))
        {
            ApplyLanding(user, safe!.Value);
            ApplyBlockedDamage(user, comp);
            return;
        }

        // 0 места для тп, назад
        _transform.SetCoordinates(user, pre);
        ApplyBlockedDamage(user, comp);
    }

    private void ApplyBlockedDamage(EntityUid user, SyndicateTeleporterComponent comp)
    {
        if (comp.DamageOnBlocked is { } dmg && HasComp<DamageableComponent>(user))
            _damage.TryChangeDamage(user, dmg);
    }

    private bool IsSpotFree(EntityUid user, EntityCoordinates coords)
    {
        if (Transform(user).MapID != coords.GetMapId(EntityManager))
            return false;

        var tile = _turf.GetTileRef(coords);
        if (tile is null || _turf.IsTileBlocked(tile.Value, CollisionGroup.Impassable))
            return false;

        var currAabb = _physics.GetWorldAABB(user);
        if (currAabb.Size == Vector2.Zero)
            return true;

        var mapCoords = _transform.ToMapCoordinates(coords);
        var half = currAabb.Size / 2f;
        var center = mapCoords.Position;

        var targetAabb = new Box2(
            center.X - half.X,
            center.Y - half.Y,
            center.X + half.X,
            center.Y + half.Y
        );

        if (!_physics.TryCollideRect(targetAabb, mapCoords.MapId))
            return true;

        static bool Overlaps(Box2 a, Box2 b) =>
            a.Left < b.Right && a.Right > b.Left && a.Bottom < b.Top && a.Top > b.Bottom;

        if (Overlaps(targetAabb, currAabb))
            return true;

        return false;
    }

    private bool TryFindSafeTile(EntityUid user, EntityCoordinates origin, out EntityCoordinates? result)
    {
        var mapId = Transform(user).MapID;

        foreach (var cand in EnumerateCandidates(origin, mapId, MaxCorrectionRadius))
        {
            if (IsSpotFree(user, cand))
            {
                result = cand;
                return true;
            }
        }

        result = null;
        return false;
    }

    private IEnumerable<EntityCoordinates> EnumerateCandidates(EntityCoordinates origin, MapId mapId, int maxRadius)
    {
        for (var radius = 1; radius <= maxRadius; radius++)
        {
            for (var i = 0; i < MaxCorrectionTries; i++)
            {
                var baseDeg = 45 * _random.Next(0, 8);
                var jitter = _random.Next(-10, 11);
                var angle = Angle.FromDegrees(baseDeg + jitter);

                var step = angle.ToWorldVec() * new Vector2(radius, radius);
                var target = origin.Offset(step);

                if (mapId != target.GetMapId(EntityManager))
                    continue;

                yield return target;
            }
        }
    }

    private void ApplyLanding(EntityUid user, EntityCoordinates where)
    {
        _transform.SetCoordinates(user, where);
        Spawn(TargetEffectPrototype, _transform.ToMapCoordinates(where));
    }
}

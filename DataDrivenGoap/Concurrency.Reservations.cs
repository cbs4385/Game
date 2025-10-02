
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using DataDrivenGoap.Core;
using DataDrivenGoap.Persistence;

namespace DataDrivenGoap.Concurrency
{
    internal struct ReservationKey : IEquatable<ReservationKey>
    {
        public readonly string Key;
        public ReservationKey(ThingId tid) { Key = tid.Value; }
        public bool Equals(ReservationKey other) => Key == other.Key;
        public override bool Equals(object obj) => obj is ReservationKey k && Equals(k);
        public override int GetHashCode() => Key != null ? Key.GetHashCode() : 0;
        public override string ToString() => Key;
    }

    internal sealed class ReservationToken
    {
        public ThingId Thing;
        public ThingId Owner;
        public Guid PlanId;
        public ReservationMode Mode;
        public int Priority;
        public DateTime CreatedUtc;
    }

    public sealed class ReservationService : IReservationService, IReservationQuery
    {
        private readonly ConcurrentDictionary<ReservationKey, ReservationToken> _tokens = new ConcurrentDictionary<ReservationKey, ReservationToken>();

        public bool TryAcquireAll(IReadOnlyList<Reservation> reservations, Guid planId, ThingId actorId)
        {
            if (reservations == null || reservations.Count == 0) return true;
            var ordered = reservations.OrderBy(r => r.Thing.Value, StringComparer.Ordinal).ToArray();
            var acquired = new List<ReservationKey>(ordered.Length);
            // Keep track of reservations already owned by this actor so we can refresh their plan id
            // once we know the entire acquisition succeeded.
            var alreadyOwned = new List<(ReservationToken token, Reservation reservation)>(ordered.Length);
            foreach (var r in ordered)
            {
                var key = new ReservationKey(r.Thing);
                var token = new ReservationToken { Thing = r.Thing, Owner = actorId, PlanId = planId, Mode = r.Mode, Priority = r.Priority, CreatedUtc = DateTime.UtcNow };
                if (_tokens.TryGetValue(key, out var existing))
                {
                    if (existing.Owner.Equals(actorId)) { alreadyOwned.Add((existing, r)); continue; }
                    if (existing.Mode == ReservationMode.Soft && r.Priority > existing.Priority)
                    {
                        if (_tokens.TryUpdate(key, token, existing)) { acquired.Add(key); continue; }
                    }
                    foreach (var a in acquired) _tokens.TryRemove(a, out _);
                    return false;
                }
                else
                {
                    if (_tokens.TryAdd(key, token)) acquired.Add(key);
                    else { foreach (var a in acquired) _tokens.TryRemove(a, out _); return false; }
                }
            }
            foreach (var owned in alreadyOwned)
            {
                owned.token.PlanId = planId;
                owned.token.Mode = owned.reservation.Mode;
                owned.token.Priority = owned.reservation.Priority;
                owned.token.CreatedUtc = DateTime.UtcNow;
            }
            return true;
        }

        public void ReleaseAll(IReadOnlyList<Reservation> reservations, Guid planId, ThingId actorId)
        {
            if (reservations == null || reservations.Count == 0) return;
            foreach (var r in reservations)
            {
                var key = new ReservationKey(r.Thing);
                if (_tokens.TryGetValue(key, out var tok))
                {
                    if (tok.Owner.Equals(actorId) && tok.PlanId == planId) _tokens.TryRemove(key, out _);
                }
            }
        }

        public bool HasActiveReservation(ThingId thing, ThingId requester)
        {
            if (thing.Value == null)
                return false;

            if (_tokens.TryGetValue(new ReservationKey(thing), out var token))
            {
                if (!token.Owner.Equals(requester) && token.Mode == ReservationMode.Hard)
                {
                    return true;
                }
            }

            return false;
        }

        public List<ReservationState> CaptureState()
        {
            var list = new List<ReservationState>();
            foreach (var kv in _tokens)
            {
                var token = kv.Value;
                if (token == null)
                    continue;
                list.Add(new ReservationState
                {
                    thing = token.Thing.Value,
                    owner = token.Owner.Value,
                    mode = token.Mode.ToString(),
                    priority = token.Priority,
                    planId = token.PlanId,
                    createdUtc = token.CreatedUtc
                });
            }
            return list;
        }

        public void ApplyState(IEnumerable<ReservationState> reservations)
        {
            _tokens.Clear();
            if (reservations == null)
                return;

            foreach (var state in reservations)
            {
                if (state == null || string.IsNullOrWhiteSpace(state.thing) || string.IsNullOrWhiteSpace(state.owner))
                    continue;
                var token = new ReservationToken
                {
                    Thing = new ThingId(state.thing.Trim()),
                    Owner = new ThingId(state.owner.Trim()),
                    PlanId = state.planId,
                    Mode = Enum.TryParse<ReservationMode>(state.mode, true, out var mode) ? mode : ReservationMode.Hard,
                    Priority = state.priority,
                    CreatedUtc = state.createdUtc
                };
                _tokens[new ReservationKey(token.Thing)] = token;
            }
        }
    }
}


using System;
using System.Collections.Generic;
using DataDrivenGoap.Core;

namespace DataDrivenGoap.Pathfinding
{
    internal static class AStar4
    {
        private struct Node : IComparable<Node>
        {
            public int X, Y;
            public int F, G;
            public int CompareTo(Node other)
            {
                int d = F - other.F; if (d != 0) return d;
                d = G - other.G; if (d != 0) return d;
                d = X - other.X; if (d != 0) return d;
                return Y - other.Y;
            }
        }

        private static readonly int[] DX = new[] { 1, -1, 0, 0 };
        private static readonly int[] DY = new[] { 0, 0, 1, -1 };

        public static bool TryFindNextStep(IWorldSnapshot s, GridPos from, GridPos to, out GridPos next)
        {
            next = from;
            if (from.Equals(to)) return true;

            int w = s.Width, h = s.Height;
            if (from.X < 0 || from.Y < 0 || from.X >= w || from.Y >= h) return false;
            if (to.X < 0 || to.Y < 0 || to.X >= w || to.Y >= h) return false;
            if (!s.IsWalkable(from) || !s.IsWalkable(to)) return false;

            var open = new SortedSet<Node>();
            var gs = new Dictionary<(int,int), int>();
            var came = new Dictionary<(int,int), (int,int)>();

            int h0 = Math.Abs(from.X - to.X) + Math.Abs(from.Y - to.Y);
            open.Add(new Node{ X=from.X, Y=from.Y, G=0, F=h0 });
            gs[(from.X,from.Y)] = 0;

            while (open.Count > 0)
            {
                var cur = open.Min; open.Remove(cur);
                if (cur.X == to.X && cur.Y == to.Y)
                {
                    var key = (cur.X, cur.Y);
                    var prev = key;
                    while (came.TryGetValue(key, out prev) && !(prev.Item1 == from.X && prev.Item2 == from.Y))
                        key = prev;
                    next = new GridPos(key.Item1, key.Item2);
                    return true;
                }
                for (int d = 0; d < 4; d++)
                {
                    int nx = cur.X + DX[d], ny = cur.Y + DY[d];
                    if (nx < 0 || ny < 0 || nx >= w || ny >= h) continue;
                    if (!s.IsWalkable(nx, ny)) continue;
                    int tg = cur.G + 1;
                    var kk = (nx, ny);
                    int old;
                    if (!gs.TryGetValue(kk, out old) || tg < old)
                    {
                        gs[kk] = tg;
                        came[kk] = (cur.X, cur.Y);
                        int hcost = Math.Abs(nx - to.X) + Math.Abs(ny - to.Y);
                        open.Add(new Node{ X=nx, Y=ny, G=tg, F=tg + hcost });
                    }
                }
            }
            return false;
        }
    }
}

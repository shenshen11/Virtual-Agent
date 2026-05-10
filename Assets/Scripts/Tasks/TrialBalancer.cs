using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace VRPerception.Tasks
{
    /// <summary>
    /// 跨被试均匀采样工具。
    /// 思路：
    ///   1. 拆出 isProtected==true 的试次（练习/锚定/baseline），原序固定置于最前；
    ///   2. 对剩余 pool 按 stratumKey 分层；每层用 taskSalt 做一次"跨被试一致"的洗牌；
    ///   3. 按层配额 k_s = round(target * |layer_s| / |pool|) 在层内做循环偏移采样：
    ///        offset_s = (participantIndex * k_s) mod |layer_s|
    ///        取 [offset_s, offset_s+k_s) 模长后的连续 k_s 项；
    ///   4. 合并各层采样结果，使用 participant 级 seed 做最终顺序洗牌（避免相邻同条件）；
    ///   5. 最终序列 = protected ⨁ sampled_main。
    /// 性质：当 N 个被试聚合后，每个 pool 项目被命中次数差 ≤ 1（近似均匀）。
    /// </summary>
    public static class TrialBalancer
    {
        public sealed class BalanceReport
        {
            public int totalProtected;
            public int totalPoolSize;
            public int totalSampled;
            public int participantIndex;
            public Dictionary<string, int> stratumSize = new Dictionary<string, int>();
            public Dictionary<string, int> stratumQuota = new Dictionary<string, int>();
            public Dictionary<string, int> stratumOffset = new Dictionary<string, int>();
        }

        /// <summary>
        /// 对 BuildTrials 输出做跨被试均衡采样。
        /// </summary>
        /// <param name="all">任务原始 BuildTrials 输出（含 protected + pool）</param>
        /// <param name="targetCount">本次被试期望的总试次数（含 protected）；≤0 或 ≥ all.Length 时直接返回 all。</param>
        /// <param name="participantIndex">被试在群体中的稳定序号（>=0）。</param>
        /// <param name="taskSalt">任务级常量盐（与 randomSeed 解耦），用于跨被试一致的层内洗牌。</param>
        /// <param name="stratumKey">分层键映射；返回 null 视为单一层（即纯均匀采样）。</param>
        /// <param name="participantOrderSeed">最终顺序洗牌的 seed（建议混入 randomSeed 与 participantIndex）。</param>
        /// <param name="report">可选：诊断信息。</param>
        public static TrialSpec[] BalanceAndSample(
            TrialSpec[] all,
            int targetCount,
            int participantIndex,
            string taskSalt,
            Func<TrialSpec, string> stratumKey,
            int participantOrderSeed,
            out BalanceReport report)
        {
            report = new BalanceReport { participantIndex = participantIndex };

            if (all == null || all.Length == 0)
            {
                return all ?? Array.Empty<TrialSpec>();
            }

            // 1. split protected vs pool
            var protectedList = new List<TrialSpec>();
            var pool = new List<TrialSpec>();
            for (int i = 0; i < all.Length; i++)
            {
                var t = all[i];
                if (t == null) continue;
                if (t.isProtected) protectedList.Add(t);
                else pool.Add(t);
            }

            report.totalProtected = protectedList.Count;
            report.totalPoolSize = pool.Count;

            // 全量或不足以触发抽样：原样返回
            if (targetCount <= 0 || targetCount >= protectedList.Count + pool.Count)
            {
                report.totalSampled = pool.Count;
                return all;
            }

            int sampleCount = Mathf.Max(0, targetCount - protectedList.Count);
            if (sampleCount <= 0)
            {
                // 目标数 ≤ protected：仅返回 protected（截到 targetCount）
                var truncated = protectedList.Take(targetCount).ToArray();
                return truncated;
            }
            if (sampleCount >= pool.Count)
            {
                // 池子全要：合并返回（仍按 protected 在前）
                var merged = new List<TrialSpec>(protectedList.Count + pool.Count);
                merged.AddRange(protectedList);
                ShuffleAvoidingAdjacent(pool, new System.Random(participantOrderSeed), stratumKey);
                merged.AddRange(pool);
                report.totalSampled = pool.Count;
                return merged.ToArray();
            }

            // 2. group by stratum key
            var groups = new Dictionary<string, List<TrialSpec>>(StringComparer.Ordinal);
            for (int i = 0; i < pool.Count; i++)
            {
                var key = stratumKey != null ? (stratumKey(pool[i]) ?? "_default") : "_default";
                if (!groups.TryGetValue(key, out var list))
                {
                    list = new List<TrialSpec>();
                    groups[key] = list;
                }
                list.Add(pool[i]);
            }

            // 跨被试一致的稳定 stratum 顺序
            var orderedKeys = groups.Keys.OrderBy(k => k, StringComparer.Ordinal).ToList();

            // 3. per-stratum salt-shuffle
            foreach (var key in orderedKeys)
            {
                int saltSeed = StableHash(taskSalt + "::" + key);
                Shuffle(groups[key], new System.Random(saltSeed));
                report.stratumSize[key] = groups[key].Count;
            }

            // 4. quota allocation: round + 1-by-1 fix to sum exactly
            var quotas = new Dictionary<string, int>(orderedKeys.Count, StringComparer.Ordinal);
            int allocated = 0;
            // first pass: floor allocation
            var fracOrder = new List<(string key, double frac)>();
            foreach (var key in orderedKeys)
            {
                double exact = (double)sampleCount * groups[key].Count / pool.Count;
                int q = (int)Math.Floor(exact);
                quotas[key] = q;
                allocated += q;
                fracOrder.Add((key, exact - q));
            }
            // distribute remaining by largest fractional remainder, ties by stable hash with participantIndex 干扰
            int remain = sampleCount - allocated;
            if (remain > 0)
            {
                var ordered = fracOrder
                    .OrderByDescending(x => x.frac)
                    .ThenBy(x => StableHash(x.key + "#" + participantIndex))
                    .ToList();
                int idx = 0;
                while (remain > 0 && idx < ordered.Count)
                {
                    var key = ordered[idx].key;
                    if (quotas[key] < groups[key].Count)
                    {
                        quotas[key] += 1;
                        remain--;
                    }
                    idx++;
                    if (idx >= ordered.Count && remain > 0)
                    {
                        // wrap: still need more, allow further +1 in same order
                        idx = 0;
                        // safety break if everyone is full
                        if (ordered.All(x => quotas[x.key] >= groups[x.key].Count)) break;
                    }
                }
            }
            else if (remain < 0)
            {
                // shouldn't happen with floor + fractional fill; safety
                var ordered = fracOrder.OrderBy(x => x.frac).ToList();
                int idx = 0;
                while (remain < 0 && idx < ordered.Count)
                {
                    var key = ordered[idx].key;
                    if (quotas[key] > 0) { quotas[key] -= 1; remain++; }
                    idx++;
                }
            }

            // 5. cyclic-offset sampling per stratum
            var sampled = new List<TrialSpec>(sampleCount);
            foreach (var key in orderedKeys)
            {
                int q = quotas[key];
                report.stratumQuota[key] = q;
                if (q <= 0) continue;
                var layer = groups[key];
                int L = layer.Count;
                // 与 participantIndex 解耦的均匀步进；选择 q 作为步长，保证不同被试覆盖不同子集
                int step = Math.Max(1, q);
                int offset = (int)(((long)participantIndex * step) % L);
                if (offset < 0) offset += L;
                report.stratumOffset[key] = offset;
                for (int r = 0; r < q; r++)
                {
                    sampled.Add(layer[(offset + r) % L]);
                }
            }

            // 6. final shuffle for presentation order (per-participant)
            var rand = new System.Random(participantOrderSeed);
            ShuffleAvoidingAdjacent(sampled, rand, stratumKey);

            // 7. concat
            var result = new List<TrialSpec>(protectedList.Count + sampled.Count);
            result.AddRange(protectedList);
            result.AddRange(sampled);
            report.totalSampled = sampled.Count;
            return result.ToArray();
        }

        /// <summary>
        /// 计算被试稳定序号：用 participantId 的稳定哈希取模 modulus（默认 10000）。
        /// 当 participantId 为空时退回 0（即"零号被试"）。
        /// </summary>
        public static int ResolveParticipantIndex(string participantId, int modulus = 10000)
        {
            if (string.IsNullOrWhiteSpace(participantId)) return 0;
            int h = StableHash(participantId);
            int m = Math.Max(1, modulus);
            int v = h % m;
            if (v < 0) v += m;
            return v;
        }

        /// <summary>FNV-1a 32-bit，进程间一致。</summary>
        public static int StableHash(string s)
        {
            unchecked
            {
                if (s == null) return 0;
                const uint offset = 2166136261;
                const uint prime = 16777619;
                uint hash = offset;
                for (int i = 0; i < s.Length; i++)
                {
                    hash ^= s[i];
                    hash *= prime;
                }
                return (int)hash;
            }
        }

        private static void Shuffle<T>(IList<T> list, System.Random rand)
        {
            if (list == null || list.Count <= 1) return;
            for (int i = list.Count - 1; i > 0; i--)
            {
                int j = rand.Next(i + 1);
                (list[i], list[j]) = (list[j], list[i]);
            }
        }

        /// <summary>
        /// 顺序洗牌，尽量避免相邻 stratumKey 相同（重试若干次后接受）。
        /// </summary>
        private static void ShuffleAvoidingAdjacent<T>(IList<T> list, System.Random rand, Func<T, string> keyFn)
            where T : class
        {
            if (list == null || list.Count <= 2) { Shuffle(list, rand); return; }
            const int maxAttempt = 32;
            for (int attempt = 0; attempt < maxAttempt; attempt++)
            {
                Shuffle(list, rand);
                if (keyFn == null) return;
                bool ok = true;
                string prev = null;
                for (int i = 0; i < list.Count; i++)
                {
                    var k = keyFn(list[i]);
                    if (i > 0 && string.Equals(k, prev, StringComparison.Ordinal)) { ok = false; break; }
                    prev = k;
                }
                if (ok) return;
            }
            // 接受最后一次
        }
    }
}

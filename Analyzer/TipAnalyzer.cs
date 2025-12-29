using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using DataSvc.Models;

namespace DataSvc.Analyzer;

public static class TipAnalyzer
{
    public sealed record ProposedResult(string Code, double Probability);
    public static List<ProposedResult> Analyze(
                        DetailsItemDto d,
                        string homeName,
                        string awayName,
                        string? proposedCode = null)
    {
        var list = Analyze(d, homeName, awayName); // core calc

        if (string.IsNullOrWhiteSpace(proposedCode)) return list;

        // --- helpers ------------------------------------------------------------
        static double Clamp01(double x) => x < 0 ? 0 : (x > 1 ? 1 : x);
        static double Sigmoid(double z) => 1.0 / (1.0 + Math.Exp(-z));
        static double Logit(double p)
        {
            // protect against 0/1 to avoid +/-infinity
            const double eps = 1e-9;
            p = Math.Min(1 - eps, Math.Max(eps, p));
            return Math.Log(p / (1 - p));
        }
        static double LogitBump(double p, double tau, double lo = 0.05, double hi = 0.95)
        {
            if (double.IsNaN(p)) return p;
            var pp = Sigmoid(Logit(p) + tau);
            return Math.Min(hi, Math.Max(lo, pp));
        }

        // normalize code formatting
        string tip = proposedCode.Trim().ToUpperInvariant();

        // index for quick lookup
        var idx = list
            .Select((r, i) => (r.Code.ToUpperInvariant(), i))
            .ToDictionary(t => t.Item1, t => t.i);

        // convenience getter/setter
        double Get(string code) => idx.TryGetValue(code.ToUpperInvariant(), out var i) ? list[i].Probability : double.NaN;
        void Set(string code, double p)
        {
            if (idx.TryGetValue(code.ToUpperInvariant(), out var i))
                list[i] = new ProposedResult(list[i].Code, Clamp01(p));
        }

        // --- apply prior --------------------------------------------------------
        // Stronger prior for 1X2, milder for single markets
        const double TAU_1X2 = 0.45;
        const double TAU_SOLO = 0.35;

        // 1X2 family handling (bump selected, then renormalize and rebuild double chance)
        if (tip is "1" or "X" or "2")
        {
            double p1 = Get("1"), px = Get("X"), p2 = Get("2");
            if (!double.IsNaN(p1 + px + p2) && p1 + px + p2 > 1e-9)
            {
                // bump selected
                if (tip == "1") p1 = LogitBump(p1, TAU_1X2);
                else if (tip == "X") px = LogitBump(px, TAU_1X2);
                else p2 = LogitBump(p2, TAU_1X2);

                // renormalize to sum=1, preserving ratio of the non-bumped two
                double s = p1 + px + p2;
                if (s > 1e-9) { p1 /= s; px /= s; p2 /= s; }

                Set("1", p1); Set("X", px); Set("2", p2);

                // rebuild double-chance
                Set("1X", Clamp01(p1 + px));
                Set("X2", Clamp01(px + p2));
                // rebuild double-chance from UPDATED 1X2
                var (p1xNew, px2New, p12New) = BuildDoubleChance(p1, px, p2);
                Set("1X", p1xNew);
                Set("X2", px2New);

                // reapply the 12 dump on the fresh p12
                double dec1 = Dc12FilterPx(px, p2);
                double dec2 = Dc12FilterHomeDom(p1, p2);
                double dec3 = Dc12FilterAwayDom(p1, p2);
                double totalDec = Math.Clamp(dec1 + dec2 + dec3, 0.0, 0.20);
                Set("12", Clamp01(p12New * (1.0 - totalDec)));

            }

            return list;
        }

        // after building idx and normalizing tip
        bool isDoubleChance = tip is "1X" or "X2" or "12";
        // Standalone markets
        // Recognized codes (case-insensitive): BTS, OTS, HTS, GTS,
        // O 1.5 / O 2.5 / O 3.5, U 1.5 / U 2.5 / U 3.5, HTO 1.5/2.5/3.5, HTU 1.5/2.5/3.5
        // If the code exists in the list, just bump it.
        if (idx.ContainsKey(tip) && !isDoubleChance)
        {
            double p = Get(tip);
            if (!double.IsNaN(p))
            {
                p = LogitBump(p, TAU_SOLO);
                Set(tip, p);

                // For totals, you can optionally keep complements coherent (e.g., U = 1 - O)
                // Try to detect matching complement and rebuild it from p to keep consistency.
                bool isOver = tip.StartsWith("O ");
                bool isUnder = tip.StartsWith("U ");
                bool isHTOver = tip.StartsWith("HTO");
                bool isHTUnder = tip.StartsWith("HTU");

                string? complement = null;
                if (isOver) complement = tip.Replace("O ", "U ");
                else if (isUnder) complement = tip.Replace("U ", "O ");
                else if (isHTOver) complement = tip.Replace("HTO", "HTU");
                else if (isHTUnder) complement = tip.Replace("HTU", "HTO");

                if (complement != null && idx.ContainsKey(complement))
                {
                    // keep a clean complement
                    Set(complement, Clamp01(1.0 - p));
                }
            }

            return list;
        }

        // Unknown code -> return original list unchanged
        return list;
    }

    public static List<ProposedResult> Analyze(DetailsItemDto d, string homeName, string awayName)
    {
        // --- 1) Build signals -------------------------------------------------
        // 1X2 charts (0..1) + conditional chart weight
        var chart1x2 = Read1X2FromCharts(d, homeName, awayName); // p's in 0..1
        var wChart1x2 = (!double.IsNaN(chart1x2.Home)
                      || !double.IsNaN(chart1x2.Draw)
                      || !double.IsNaN(chart1x2.Away)) ? 2.0 : 0.0;

        // BTTS / OTS charts (0..1) + conditional weights
        double chartBtts = ReadTeamToScore(d, "both") / 100.0;
        double wChartBtts = double.IsNaN(chartBtts) ? 0.0 : 2.0;

        double chartOts = ReadTeamToScore(d, "only one") / 100.0;
        double wChartOts = double.IsNaN(chartOts) ? 0.0 : 2.0;

        // O/U charts (Over side only; Under is 1-Over) + conditional weights
        double chartOU15 = ReadOverUnder(d, 1.5, true) / 100.0;
        double wChartOU15 = double.IsNaN(chartOU15) ? 0.0 : 2.0;

        double chartOU25 = ReadOverUnder(d, 2.5, true) / 100.0;
        double wChartOU25 = double.IsNaN(chartOU25) ? 0.0 : 2.0;

        double chartOU35 = ReadOverUnder(d, 3.5, true) / 100.0;
        double wChartOU35 = double.IsNaN(chartOU35) ? 0.0 : 2.0;

        // Historical signals
        var h2h = ComputeH2H(d, homeName, awayName);             // includes 1X2 + OU + BTTS from H2H
        var sepHome = ComputeTeamRatesFromSeparate(d, homeName);
        var sepAway = ComputeTeamRatesFromSeparate(d, awayName);
        var factsHome = ComputeTeamRatesFromFacts(d, homeName);
        var factsAway = ComputeTeamRatesFromFacts(d, awayName);

        // Poisson-from-facts O/U hints (0..1), shrink gamma as desired (0.85–0.95 typical)
        double o15Facts = FactsOver(factsHome.ScoreChance, factsAway.ConcedeChance,
                                    factsAway.ScoreChance, factsHome.ConcedeChance,
                                    line: 1.5, gamma: 0.90);

        double o25Facts = FactsOver(factsHome.ScoreChance, factsAway.ConcedeChance,
                                    factsAway.ScoreChance, factsHome.ConcedeChance,
                                    line: 2.5, gamma: 0.90);

        double o35Facts = FactsOver(factsHome.ScoreChance, factsAway.ConcedeChance,
                                    factsAway.ScoreChance, factsHome.ConcedeChance,
                                    line: 3.5, gamma: 0.90);

        // --- Standings-based Poisson + motivation (NEW) ----------------------
        double wStand = 0;
        double o15Stand = double.NaN, o25Stand = double.NaN, o35Stand = double.NaN;
        double htsStand = double.NaN, gtsStand = double.NaN, bttsStand = double.NaN;
        double p1Stand = double.NaN, pxStand = double.NaN, p2Stand = double.NaN;

        var stCtx = TryReadStandings(d, homeName, awayName);
        if (stCtx is not null)
        {
            // Pressure: relegation/Europe (top tier guess) scaled by time & gaps
            double pressureHome = 0, pressureAway = 0;
            int seasonMatches = (stCtx.Rows.Count == 18) ? 34 : 38;
            bool topTier = true; // if you can detect tiers, set appropriately
            var rules = GuessRules(stCtx.Rows.Count, topTier);

            int safeRank = Math.Max(1, stCtx.Rows.Count - rules.RelegationSlots);
            int safePts = stCtx.Rows.FirstOrDefault(r => r.Position == safeRank)?.Points ?? stCtx.Home.Points;

            pressureHome = PressureScore(stCtx.Home.Position, stCtx.Home.Points, stCtx.Home.Matches,
                                         rules, targetUp: true, targetRank: safeRank, targetPts: safePts,
                                         seasonMatches: seasonMatches);
            pressureAway = PressureScore(stCtx.Away.Position, stCtx.Away.Points, stCtx.Away.Matches,
                                         rules, targetUp: true, targetRank: safeRank, targetPts: safePts,
                                         seasonMatches: seasonMatches);

            // Lambdas from standings (attack/defense) + apply motivation multipliers
            var (lamH0, lamA0, w) = LambdasFromStandings(stCtx, hfa: 1.10, shrink: 0.70);
            var (attH, attA, drawM) = MotivationMultipliers(pressureHome, pressureAway);
            double lamH = lamH0 * attH;
            double lamA = lamA0 * attA;
            wStand = w;

            // Turn lambdas into market hints
            o15Stand = OverFromLambda(lamH + lamA, 1.5);
            o25Stand = OverFromLambda(lamH + lamA, 2.5);
            o35Stand = OverFromLambda(lamH + lamA, 3.5);

            htsStand = 1 - Math.Exp(-lamH);
            gtsStand = 1 - Math.Exp(-lamA);
            bttsStand = htsStand * gtsStand;

            (p1Stand, pxStand, p2Stand) = OneXTwoFromLambdas(lamH, lamA);
            pxStand = Clamp01(pxStand * drawM);       // soften draw if either side “must win”

            // Renormalize standings 1X2 hint to a proper distribution
            double s1x2 = p1Stand + pxStand + p2Stand;
            if (s1x2 > 1e-9) { p1Stand /= s1x2; pxStand /= s1x2; p2Stand /= s1x2; }
        }

        // --- 2) Derive market probabilities from signals ---------------------

        // 1X2 from: charts + H2H outcomes + separate (wins/draws/loss) + facts (wins/draws/loss) + standings hint
        var p1 = Blend(new[]
        {
            C(chart1x2.Home,   w: wChart1x2),
            C(h2h.PHome,       w: wSqrt(h2h.NH2H)),
            C(Avg( sepHome.WinRate,  sepAway.LossRate ), w: wSqrt(sepHome.N + sepAway.N)),
            C(Avg(factsHome.WinRate, factsAway.LossRate), w: wSqrt(factsHome.N + factsAway.N)),
            C(p1Stand,         w: wStand) // NEW
        });

        var px = Blend(new[]
                {
            C(chart1x2.Draw,   w: wChart1x2),
            C(h2h.PDraw,       w: wSqrt(h2h.NH2H)),
            C(Avg( sepHome.DrawRate,  sepAway.DrawRate ), w: wSqrt(sepHome.N + sepAway.N)),
            C(Avg(factsHome.DrawRate, factsAway.DrawRate), w: wSqrt(factsHome.N + factsAway.N)),
            C(pxStand,         w: wStand) // NEW
        });

        var p2 = Blend(new[]
        {
            C(chart1x2.Away,   w: wChart1x2),
            C(h2h.PAway,       w: wSqrt(h2h.NH2H)),
            C(Avg( sepAway.WinRate,  sepHome.LossRate ), w: wSqrt(sepHome.N + sepAway.N)),
            C(Avg(factsAway.WinRate, factsHome.LossRate), w: wSqrt(factsHome.N + factsAway.N)),
            C(p2Stand,         w: wStand) // NEW
        });
        // After the three Blend(...) assignments:
        (p1, px, p2) = Normalize1X2(p1, px, p2);

        // Initial double-chance (coherent even if standings are missing)
        var (p1x, px2, p12) = BuildDoubleChance(p1, px, p2);
        /*
        // Double chance
        var p1x = Clamp01(p1 + px);
        var px2 = Clamp01(px + p2);
        var p12 = Clamp01(p1 + p2);
        */
        // BTTS / OTS with standings hint
        var btts = Blend(new[]
        {
            C(chartBtts,                       w: wChartBtts),
            C(h2h.PBTTS,                       w: wSqrt(h2h.NH2H)),
            // Independence-ish: P(BTTS) ≈ P(home scores)*P(away scores)
            C(sepHome.ScoreRate * sepAway.ScoreRate,                  w: wSqrt(sepHome.N + sepAway.N)),
            C(factsHome.ScoreChance * factsAway.ScoreChance,          w: 1.5),
            C(factsHome.ConcedeChance * factsAway.ConcedeChance,      w: 1.5),
            C(bttsStand,                                              w: wStand) // NEW
        });

        var ots = Blend(new[]
        {
            C(chartOts,                        w: wChartOts),
            C(h2h.POnlyOne,                    w: wSqrt(h2h.NH2H)),
            // ≈ P(home scores)*(1-P(away scores)) + P(away scores)*(1-P(home scores))
            C(sepHome.ScoreRate*(1-sepAway.ScoreRate) + sepAway.ScoreRate*(1-sepHome.ScoreRate),
              w: wSqrt(sepHome.N + sepAway.N))
            // (You can add a facts-only OTS proxy if you want; left as-is)
        });

        // HTS/GTS with standings hint
        var hts = Blend(new[]
        {
            C(Avg(factsHome.ScoreChance, factsAway.ConcedeChance), w: 2),
            C(h2h.PHomeScored,                                     w: wSqrt(h2h.NH2H)),
            C(Avg(sepHome.ScoreRate,   sepAway.ConcedeRate),       w: wSqrt(sepHome.N + sepAway.N)),
            C(htsStand,                                            w: wStand) // NEW
        });

        var gts = Blend(new[]
        {
            C(Avg(factsAway.ScoreChance, factsHome.ConcedeChance), w: 2),
            C(h2h.PAwayScored,                                     w: wSqrt(h2h.NH2H)),
            C(Avg(sepAway.ScoreRate,   sepHome.ConcedeRate),       w: wSqrt(sepHome.N + sepAway.N)),
            C(gtsStand,                                            w: wStand) // NEW
        });

        // Over/Under totals: chart + H2H + separate + facts (2.5) + facts-Poisson + standings-Poisson
        var o15 = Blend(new[]
        {
            C(chartOU15,                                     w: wChartOU15),
            C(h2h.POver15,                                   w: wSqrt(h2h.NH2H)),
            C(Avg(sepHome.Over15Rate, sepAway.Over15Rate),   w: wSqrt(sepHome.N + sepAway.N)),
            C(o15Facts,                                      w: 1.3), // facts→Poisson
            C(o15Stand,                                      w: Math.Max(1.0, 0.8*wStand)) // NEW
        });
        var u15 = 1 - o15;

        var o25 = Blend(new[]
        {
            C(chartOU25,                                     w: wChartOU25),
            C(h2h.POver25,                                   w: wSqrt(h2h.NH2H)),
            C(Avg(sepHome.Over25Rate, sepAway.Over25Rate),   w: wSqrt(sepHome.N + sepAway.N)),
            C(Avg(factsHome.Over25RateFacts, factsAway.Over25RateFacts), w: wSqrt(factsHome.N + factsAway.N)),
            C(o25Facts,                                      w: 1.3), // facts→Poisson
            C(o25Stand,                                      w: Math.Max(1.0, 0.8*wStand)) // NEW
        });
        var u25 = 1 - o25;

        var o35 = Blend(new[]
        {
            C(chartOU35,                                     w: wChartOU35),
            C(h2h.POver35,                                   w: wSqrt(h2h.NH2H)),
            C(Avg(sepHome.Over35Rate, sepAway.Over35Rate),   w: wSqrt(sepHome.N + sepAway.N)),
            C(o35Facts,                                      w: 1.3), // facts→Poisson
            C(o35Stand,                                      w: Math.Max(1.0, 0.8*wStand)) // NEW
        });
        var u35 = 1 - o35;

        // ==================== Positive-only overlay ===========================
        // Only allow standings/motivation to push "positive" outcomes up (and draw down).

        double UpFactor(double w) => Math.Min(0.35, 0.10 + 0.10 * w); // 0.10..0.30+

        void UpOnly(ref double baseVal, double hint, double w)
        {
            if (!double.IsNaN(hint) && hint > baseVal)
            {
                double a = UpFactor(w);
                baseVal = Clamp01(baseVal + a * (hint - baseVal));
            }
        }

        void DownOnlyDraw(ref double p1v, ref double pxv, ref double p2v, double pxHintV, double w)
        {
            if (!double.IsNaN(pxHintV) && pxHintV < pxv)
            {
                double a = UpFactor(w);
                double newPx = Clamp01(pxv - a * (pxv - pxHintV));
                double sNonDraw = p1v + p2v;
                if (sNonDraw > 1e-9)
                {
                    double scale = (1.0 - newPx) / sNonDraw;
                    p1v *= scale; p2v *= scale; pxv = newPx;
                }
                else
                {
                    pxv = newPx;
                }
            }
        }

        // Over lines: push up only if standings Poisson suggests higher totals
        UpOnly(ref o15, o15Stand, wStand);
        UpOnly(ref o25, o25Stand, wStand);
        UpOnly(ref o35, o35Stand, wStand);
        // Under complements
        u15 = 1 - o15; u25 = 1 - o25; u35 = 1 - o35;

        // HTS / GTS / BTTS: push up only
        UpOnly(ref hts, htsStand, wStand);
        UpOnly(ref gts, gtsStand, wStand);
        UpOnly(ref btts, bttsStand, wStand);

        // Draw: push DOWN only; redistribute mass to 1 and 2
        DownOnlyDraw(ref p1, ref px, ref p2, pxStand, wStand);

        (p1, px, p2) = Normalize1X2(p1, px, p2);

        // Rebuild DC from complements to stay consistent
        (p1x, px2, p12) = BuildDoubleChance(p1, px, p2);

        /*
        // Rebuild double-chance from updated 1X2
        p1x = Clamp01(p1 + px);
        px2 = Clamp01(px + p2);
        p12 = Clamp01(p1 + p2);
        */

        // Half-time O/U not reliably present in your feed -> NaN
        double hto15 = double.NaN, hto25 = double.NaN, hto35 = double.NaN;
        double htu15 = double.NaN, htu25 = double.NaN, htu35 = double.NaN;
        // === DC '12' filters & pumps (no feedback into 1X2/DC normalization) ===
        double dec1 = Dc12FilterPx(px, p2);
        double dec2 = Dc12FilterHomeDom(p1, p2);
        double dec3 = Dc12FilterAwayDom(p1, p2);
        double totalDec = Math.Clamp(dec1 + dec2 + dec3, 0.0, 0.20);
        double p12_adj = Clamp01(p12 * (1.0 - totalDec));

        // Presentation-only pumps tied to the filters (do not alter p1/px/p2 used for DC)
        double p1_disp = Clamp01(p1 * (1.0 + dec1 + dec2));
        double px_disp = Clamp01(px * (1.0 + dec1));
        double p2_disp = Clamp01(p2 * (1.0 + dec3));
        double hts_disp = Clamp01(hts * (1.0 + dec1 + dec2));
        double gts_disp = Clamp01(gts * (1.0 + dec3));

        // Right before the return at the end of Analyze(d, home, away):
        var results = new List<ProposedResult>
        {
            new("1", p1_disp), new("X", px_disp), new("2", p2_disp),
            new("1X",  p1x), new("X2", px2), new("12", p12_adj),

            new("BTS", btts), new("OTS", ots),
            new("HTS", hts_disp),  new("GTS", gts_disp),

            new("O 1.5", o15), new("O 2.5", o25), new("O 3.5", o35),
            new("U 1.5", u15), new("U 2.5", u25), new("U 3.5", u35),

            new("HTO 1.5", hto15), new("HTO 2.5", hto25), new("HTO 3.5", hto35),
            new("HTU 1.5", htu15), new("HTU 2.5", htu25), new("HTU 3.5", htu35),
        };

        // Hide empty markets entirely
        results = results.Where(r => !double.IsNaN(r.Probability)).ToList();

        return results;
    }


    // ====================== helpers ======================
    // Keep it near the other helpers
    private static (double p1, double px, double p2) Normalize1X2(double p1, double px, double p2)
    {
        // treat NaNs as 0 in the sum, but preserve them for output if everything is NaN
        double s = 0;
        bool v1 = !double.IsNaN(p1), vx = !double.IsNaN(px), v2 = !double.IsNaN(p2);
        if (v1) s += p1;
        if (vx) s += px;
        if (v2) s += p2;

        if (s <= 1e-12)
            return (double.NaN, double.NaN, double.NaN);

        double scale = 1.0 / s;
        double n1 = v1 ? Clamp01(p1 * scale) : double.NaN;
        double nx = vx ? Clamp01(px * scale) : double.NaN;
        double n2 = v2 ? Clamp01(p2 * scale) : double.NaN;

        // if any was NaN, re-fill missing piece so total is ~1
        int cnt = (v1 ? 1 : 0) + (vx ? 1 : 0) + (v2 ? 1 : 0);
        if (cnt == 2)
        {
            if (!v1 && !double.IsNaN(nx) && !double.IsNaN(n2)) n1 = Clamp01(1 - nx - n2);
            if (!vx && !double.IsNaN(n1) && !double.IsNaN(n2)) nx = Clamp01(1 - n1 - n2);
            if (!v2 && !double.IsNaN(n1) && !double.IsNaN(nx)) n2 = Clamp01(1 - n1 - nx);
        }

        return (n1, nx, n2);
    }

    private static (double p1x, double px2, double p12) BuildDoubleChance(double p1, double px, double p2)
    {
        // Prefer complements to eliminate rounding drift
        // If something is NaN, fall back to sums where possible.
        double one(double x) => double.IsNaN(x) ? double.NaN : x;
        double p1x = !double.IsNaN(p2) ? Clamp01(1 - p2) : (double.IsNaN(p1) || double.IsNaN(px) ? double.NaN : Clamp01(p1 + px));
        double px2 = !double.IsNaN(p1) ? Clamp01(1 - p1) : (double.IsNaN(px) || double.IsNaN(p2) ? double.NaN : Clamp01(px + p2));
        double p12 = !double.IsNaN(px) ? Clamp01(1 - px) : (double.IsNaN(p1) || double.IsNaN(p2) ? double.NaN : Clamp01(p1 + p2));
        return (p1x, px2, p12);
    }


    private static double Clamp01(double x) => x < 0 ? 0 : (x > 1 ? 1 : x);
    private static double AvgOld(double a, double b) => (a + b) / 2.0;
    // Replace the current Avg
    private static double Avg(double a, double b)
    {
        bool aOk = !double.IsNaN(a);
        bool bOk = !double.IsNaN(b);
        if (aOk && bOk) return (a + b) / 2.0;
        if (aOk) return a;
        if (bOk) return b;
        return double.NaN;
    }

    private static double wSqrt(int n) => Math.Sqrt(Math.Max(0, n)); // adaptive weight

    private sealed record Contrib(double P, double W);
    private static Contrib C(double p, double w) => new(Clamp01(p), w);
    private static double Blend(IEnumerable<Contrib> parts)
    {
        double sumW = 0, sumPW = 0;
        foreach (var (p, w) in parts.Where(x => !double.IsNaN(x.P) && x.W > 0))
        {
            sumPW += p * w;
            sumW += w;
        }
        return sumW <= 0 ? double.NaN : Clamp01(sumPW / sumW);
    }

    // ---------- CHART READS ----------
    private static (double Home, double Draw, double Away) Read1X2FromCharts(DetailsItemDto d, string home, string away)
    {
        double? c1_home = ReadChartPercent(d, "1 X 2", "HalfContainer1", home);
        double? c1_draw = ReadChartPercent(d, "1 X 2", "HalfContainer1", "draw");
        double? c1_away = ReadChartPercent(d, "1 X 2", "HalfContainer1", "opponent");

        double? c2_home = ReadChartPercent(d, "1 X 2", "HalfContainer2", "opponent");
        double? c2_draw = ReadChartPercent(d, "1 X 2", "HalfContainer2", "draw");
        double? c2_away = ReadChartPercent(d, "1 X 2", "HalfContainer2", away);

        double homeP = AvgPct(c1_home, c2_home);
        double drawP = AvgPct(c1_draw, c2_draw);
        double awayP = AvgPct(c1_away, c2_away);

        return (homeP / 100.0, drawP / 100.0, awayP / 100.0);
    }

    private static double ReadTeamToScore(DetailsItemDto d, string item)
    {
        var c1 = ReadChartPercent(d, "Team to score", "HalfContainer1", item);
        var c2 = ReadChartPercent(d, "Team to score", "HalfContainer2", item);
        return AvgPct(c1, c2);
    }

    private static double ReadOverUnder(DetailsItemDto d, double line, bool wantOver)
    {
        string title = $"Over/under {line.ToString(CultureInfo.InvariantCulture)} for all goals in matches";
        string item = wantOver ? "over" : "under";
        var c1 = ReadChartPercent(d, title, "HalfContainer1", item);
        var c2 = ReadChartPercent(d, title, "HalfContainer2", item);
        return AvgPct(c1, c2);
    }

    private static double FactsOver(double pScoreHome, double pConcedeAway,
                            double pScoreAway, double pConcedeHome,
                            double line, double gamma = 0.9)
    {
        // If any input is missing, skip this term
        if (double.IsNaN(pScoreHome) || double.IsNaN(pConcedeAway) ||
            double.IsNaN(pScoreAway) || double.IsNaN(pConcedeHome))
            return double.NaN;

        return OverFromFacts(pScoreHome, pConcedeAway, pScoreAway, pConcedeHome, line, gamma);
    }

    static double OverFromFacts(double pScoreHome, double pConcedeAway,
                        double pScoreAway, double pConcedeHome,
                        double line, double gamma = 0.9)
    {
        double pH = Clamp01((pScoreHome + pConcedeAway) / 2.0);
        double pA = Clamp01((pScoreAway + pConcedeHome) / 2.0);

        double lamH = -Math.Log(Math.Max(1e-9, 1 - pH));
        double lamA = -Math.Log(Math.Max(1e-9, 1 - pA));
        double lamT = gamma * (lamH + lamA);

        int k = line switch { 1.5 => 1, 2.5 => 2, 3.5 => 3, 0.5 => 0, _ => (int)Math.Floor(line) };
        double cdf = 0.0;
        double term = Math.Exp(-lamT);
        for (int i = 0; i <= k; i++)
        {
            if (i > 0) term *= lamT / i;
            cdf += term;
        }
        return Clamp01(1.0 - cdf);
    }

    private static double AvgPctOld(double? a, double? b)
    {
        var xs = new[] { a, b }.Where(v => v.HasValue).Select(v => v!.Value).ToArray();
        if (xs.Length == 0) return 0;
        return xs.Average();
    }
    // Replace the current AvgPct
    private static double AvgPct(double? a, double? b)
    {
        var xs = new[] { a, b }.Where(v => v.HasValue).Select(v => v!.Value).ToArray();
        if (xs.Length == 0) return double.NaN;   // was 0 -> NaN so Blend ignores it
        return xs.Average();
    }


    private static double? ReadChartPercent(DetailsItemDto d, string title, string halfContainerId, string itemName)
    {
        if (d?.BarCharts == null) return null;
        var chart = d.BarCharts.FirstOrDefault(c =>
            c.Title?.Equals(title, StringComparison.OrdinalIgnoreCase) == true &&
            c.HalfContainerId?.Equals(halfContainerId, StringComparison.OrdinalIgnoreCase) == true);

        var item = chart?.Items?.FirstOrDefault(i =>
            i.Name?.Equals(itemName, StringComparison.OrdinalIgnoreCase) == true);

        return item?.Percentage;
    }

    // ---------- H2H AGGREGATION ----------
    private sealed record H2HStats(
        int NH2H,
        double PHome, double PDraw, double PAway,
        double POver15, double POver25, double POver35,
        double PBTTS, double POnlyOne,
        double PHomeScored, double PAwayScored
    );

    private static H2HStats ComputeH2H(DetailsItemDto d, string home, string away)
    {
        var matches = d?.MatchDataBetween?.Matches ?? new List<MatchBetweenItemDto>();
        int n = 0, hW = 0, dW = 0, aW = 0, over15 = 0, over25 = 0, over35 = 0, btts = 0, onlyOne = 0, hScored = 0, aScored = 0;

        foreach (var m in matches)
        {
            if (!TryInt(m.HostGoals, out var hg) || !TryInt(m.GuestGoals, out var gg)) continue;
            string host = m.HostTeam ?? "", guest = m.GuestTeam ?? "";
            if (!(SameTeam(host, home) && SameTeam(guest, away)) &&
                !(SameTeam(host, away) && SameTeam(guest, home))) continue;

            n++;
            int total = hg + gg;
            if (hg > gg) { if (SameTeam(host, home)) hW++; else aW++; }
            else if (hg < gg) { if (SameTeam(guest, home)) hW++; else aW++; }
            else dW++;

            if (total > 1) over15++;
            if (total > 2) over25++;
            if (total > 3) over35++;

            bool hs = (SameTeam(host, home) ? hg : gg) > 0;
            bool as_ = (SameTeam(host, home) ? gg : hg) > 0;
            if (hs) hScored++;
            if (as_) aScored++;

            if (hg > 0 && gg > 0) btts++;
            if ((hg > 0) ^ (gg > 0)) onlyOne++;
        }

        double toPOld(int x) => n == 0 ? 0 : (double)x / n;
        // In ComputeH2H, replace the local toP:
        double toP(int x) => n == 0 ? double.NaN : (double)x / n;  // was 0

        return new H2HStats(
            n,
            toP(hW), toP(dW), toP(aW),
            toP(over15), toP(over25), toP(over35),
            toP(btts), toP(onlyOne),
            toP(hScored), toP(aScored)
        );
    }

    // ---------- SEPARATE MATCHES + FACTS ----------
    private sealed record TeamRates(
        int N,
        double WinRate, double DrawRate, double LossRate,
        double ScoreRate, double ConcedeRate,
        double Over15Rate, double Over25Rate, double Over35Rate,
        double Over25RateFacts, double ScoreChance, double ConcedeChance
    );

    private static TeamRates ComputeTeamRatesFromSeparate(DetailsItemDto d, string team)
    {
        var all = d?.RecentMatchesSeparate?.Matches ?? new List<MatchBetweenItemDto>();
        int n = 0, wins = 0, draws = 0, losses = 0, scored = 0, conceded = 0, ov15 = 0, ov25 = 0, ov35 = 0;

        foreach (var m in all)
        {
            if (!IsTeamInMatch(m, team)) continue;
            if (!TryInt(m.HostGoals, out var hg) || !TryInt(m.GuestGoals, out var gg)) continue;

            bool teamIsHost = SameTeam(m.HostTeam, team);
            int forG = teamIsHost ? hg : gg;
            int agG = teamIsHost ? gg : hg;

            n++;
            if (forG > agG) wins++; else if (forG == agG) draws++; else losses++;
            if (forG > 0) scored++;
            if (agG > 0) conceded++;
            int total = hg + gg;
            if (total > 1) ov15++;
            if (total > 2) ov25++;
            if (total > 3) ov35++;
        }

        double toPOld(int x) => n == 0 ? 0 : (double)x / n;
        // In ComputeTeamRatesFromSeparate, replace the local toP:
        double toP(int x) => n == 0 ? double.NaN : (double)x / n;  // was 0

        return new TeamRates(
            N: n,
            WinRate: toP(wins), DrawRate: toP(draws), LossRate: toP(losses),
            ScoreRate: toP(scored), ConcedeRate: toP(conceded),
            Over15Rate: toP(ov15), Over25Rate: toP(ov25), Over35Rate: toP(ov35),
            Over25RateFacts: double.NaN, // filled by facts path
            ScoreChance: double.NaN,
            ConcedeChance: double.NaN
        );
    }

    private static TeamRates ComputeTeamRatesFromFacts(DetailsItemDto d, string team)
    {
        var t = d?.TeamsStatistics?.FirstOrDefault(x => SameTeam(x.TeamName, team));
        if (t?.FactItems == null) return new TeamRates(0, 0, 0, 0, 0, 0, 0, 0, 0, double.NaN, double.NaN, double.NaN);

        int wins = (int?)t.FactItems.FirstOrDefault(f => f.Label?.StartsWith("Number of", StringComparison.OrdinalIgnoreCase) == true &&
                                                            f.Label.Contains("wins", StringComparison.OrdinalIgnoreCase))?.Value ?? 0;
        int draws = (int?)t.FactItems.FirstOrDefault(f => f.Label?.StartsWith("Number of", StringComparison.OrdinalIgnoreCase) == true &&
                                                            f.Label.Contains("draws", StringComparison.OrdinalIgnoreCase))?.Value ?? 0;
        int losses = (int?)t.FactItems.FirstOrDefault(f => f.Label?.StartsWith("Number of", StringComparison.OrdinalIgnoreCase) == true &&
                                                            f.Label.Contains("loses", StringComparison.OrdinalIgnoreCase))?.Value ?? 0;
        int n = Math.Max(0, wins + draws + losses);

        int over25 = (int?)t.FactItems.FirstOrDefault(f => f.Label?.Contains("Matches over 2.5 goals", StringComparison.OrdinalIgnoreCase) == true)?.Value ?? 0;
        int under25 = (int?)t.FactItems.FirstOrDefault(f => f.Label?.Contains("Matches under 2.5 goals", StringComparison.OrdinalIgnoreCase) == true)?.Value ?? 0;

        double scoreChance = (double?)t.FactItems.FirstOrDefault(f => f.Label?.Equals("Chance to score goal next match", StringComparison.OrdinalIgnoreCase) == true)?.Value ?? double.NaN;
        double concedeChance = (double?)t.FactItems.FirstOrDefault(f => f.Label?.Equals("Chance to conceded goal next match", StringComparison.OrdinalIgnoreCase) == true)?.Value ?? double.NaN;

        double toPOld(int x) => n == 0 ? 0 : (double)x / n;
        double over25RateFactsOlD = (n == 0) ? double.NaN : (double)over25 / Math.Max(1, over25 + under25);
        // In ComputeTeamRatesFromFacts, replace both spots:
        double toP(int x) => n == 0 ? double.NaN : (double)x / n;  // Win/Draw/Loss rates
        double over25RateFacts = (over25 + under25) == 0 ? double.NaN : (double)over25 / (over25 + under25);


        return new TeamRates(
            N: n,
            WinRate: toP(wins), DrawRate: toP(draws), LossRate: toP(losses),
            ScoreRate: double.IsNaN(scoreChance) ? double.NaN : Clamp01(scoreChance / 100.0), // optional mapping
            ConcedeRate: double.IsNaN(concedeChance) ? double.NaN : Clamp01(concedeChance / 100.0), // optional mapping
            Over15Rate: double.NaN, Over25Rate: double.NaN, Over35Rate: double.NaN,
            Over25RateFacts: over25RateFacts,
            ScoreChance: double.IsNaN(scoreChance) ? double.NaN : Clamp01(scoreChance / 100.0),
            ConcedeChance: double.IsNaN(concedeChance) ? double.NaN : Clamp01(concedeChance / 100.0)
        );
    }

    private static bool IsTeamInMatch(MatchBetweenItemDto m, string team)
        => SameTeam(m.HostTeam, team) || SameTeam(m.GuestTeam, team);

    private static bool SameTeam(string? a, string? b)
    {
        static string N(string s)
        {
            s = (s ?? "").Trim().ToLowerInvariant();
            var sb = new StringBuilder(s.Length);
            foreach (char ch in s)
                if (!char.IsPunctuation(ch)) sb.Append(ch == ' ' ? ' ' : ch);
            return string.Join(' ', sb.ToString().Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries));
        }
        return N(a ?? "") == N(b ?? "");
    }

    private static bool TryInt(object? val, out int num)
    {
        if (val is int i) { num = i; return true; }
        if (val is string s && int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var j))
        { num = j; return true; }
        num = 0; return false;
    }

    // ====================== Standings + Motivation helpers ======================

    // Lightweight league rules guess (you can swap with a concrete per-league map)
    public sealed record LeagueRules(
        int TotalTeams,
        int RelegationSlots,
        (int from, int to)? PromoPlayoff,
        int AutoPromoSlots = 0,
        int EuropeSlots = 0
    );

    static LeagueRules GuessRules(int totalTeams, bool topTier)
    {
        if (topTier && totalTeams == 20) return new(totalTeams, RelegationSlots: 3, PromoPlayoff: null, AutoPromoSlots: 0, EuropeSlots: 4);
        if (!topTier && totalTeams == 24) return new(totalTeams, RelegationSlots: 3, PromoPlayoff: (3, 6), AutoPromoSlots: 2, EuropeSlots: 0);
        return new(totalTeams, RelegationSlots: Math.Max(2, totalTeams / 10), PromoPlayoff: null, AutoPromoSlots: 0, EuropeSlots: topTier ? 4 : 0);
    }

    static double PressureScore(int teamRank, int teamPts, int matchesPlayed,
                                LeagueRules rules, bool targetUp, int targetRank, int targetPts,
                                int seasonMatches = 38)
    {
        int remaining = Math.Max(0, seasonMatches - matchesPlayed);
        int rankGap = targetUp ? Math.Max(0, teamRank - targetRank) : Math.Max(0, targetRank - teamRank);
        int ptsGap = Math.Max(0, targetUp ? (targetPts - teamPts) : (teamPts - targetPts));

        double r = rankGap / Math.Max(1.0, rules.TotalTeams - 1);
        double p = ptsGap / Math.Max(1.0, 3 * remaining + 1);
        double time = 1.0 - (double)remaining / Math.Max(1, seasonMatches);

        return Math.Max(0, Math.Min(1, 0.5 * r + 0.5 * p)) * (0.5 + 0.5 * time);
    }

    // Return (attackMultHome, attackMultAway, drawMult)
    static (double attH, double attA, double drawMult) MotivationMultipliers(double pressureHome, double pressureAway)
    {
        double baseBoost = 0.10; // up to +10% attack for each side
        double attH = 1.0 + baseBoost * pressureHome;
        double attA = 1.0 + baseBoost * pressureAway;

        double drawCut = 0.10 * Math.Max(pressureHome, pressureAway); // up to -10% draw
        double drawMult = 1.0 - drawCut;

        return (attH, attA, drawMult);
    }

    // Reflection-safe standings reader (handles TeamStandings.Standings with Overall.GoalsScored/Conceded)
    private sealed record StandRowProxy(int Position, string TeamName, int Matches, int Points, int GF, int GA);
    private sealed record StandingsContext(StandRowProxy Home, StandRowProxy Away, List<StandRowProxy> Rows, double LeagueMu);

    private static StandingsContext? TryReadStandings(DetailsItemDto d, string homeName, string awayName)
    {
        object? ts = GetProp(d, "TeamStandings") ?? GetProp(d, "teamStandings");
        if (ts is null) return null;

        var rowsObj = GetProp(ts, "Standings") ?? GetProp(ts, "standings");
        if (rowsObj is not System.Collections.IEnumerable list) return null;

        var rows = new List<StandRowProxy>();
        foreach (var r in list)
        {
            bool isHeader = GetBool(r, "IsHeader");
            string team = (GetString(r, "TeamName") ?? "").Trim();
            if (isHeader || string.IsNullOrWhiteSpace(team)) continue;

            int pos = GetInt(r, "Position");
            int matches = GetInt(r, "Matches");
            int points = GetInt(r, "Points");

            var overall = GetProp(r, "Overall");
            int gf = overall is null ? 0 : GetInt(overall, "GoalsScored");
            int ga = overall is null ? 0 : GetInt(overall, "GoalsConceded");

            rows.Add(new StandRowProxy(pos, team, matches, points, gf, ga));
        }

        if (rows.Count == 0) return null;

        var home = rows.FirstOrDefault(x => SameTeam(x.TeamName, homeName));
        var away = rows.FirstOrDefault(x => SameTeam(x.TeamName, awayName));
        if (home is null || away is null) return null;

        double sumGF = rows.Sum(r => (double)r.GF);
        double sumP = rows.Sum(r => (double)Math.Max(0, r.Matches));
        double mu = (sumGF > 0 && sumP > 0) ? sumGF / sumP : 1.30; // team goals/match (fallback)

        return new StandingsContext(home, away, rows, mu);
    }

    private static object? GetProp(object obj, string name) => obj.GetType().GetProperty(name)?.GetValue(obj);
    private static int GetInt(object obj, string name) => int.TryParse(Convert.ToString(GetProp(obj, name)), out var v) ? v : 0;
    private static string? GetString(object obj, string name) => Convert.ToString(GetProp(obj, name));
    private static bool GetBool(object obj, string name) => bool.TryParse(Convert.ToString(GetProp(obj, name)), out var b) && b;

    // Build lambdas from standings attack/defense multipliers + HFA, with shrinkage
    private static (double lamH, double lamA, double weight) LambdasFromStandings(StandingsContext s, double hfa = 1.10, double shrink = 0.70)
    {
        double mu = Math.Max(0.4, Math.Min(2.0, s.LeagueMu)); // keep sane (team goals/match)

        double GFh = (double)s.Home.GF / Math.Max(1, s.Home.Matches);
        double GAh = (double)s.Home.GA / Math.Max(1, s.Home.Matches);
        double GFa = (double)s.Away.GF / Math.Max(1, s.Away.Matches);
        double GAa = (double)s.Away.GA / Math.Max(1, s.Away.Matches);

        double Ah = SafeDiv(GFh, mu);
        double Va = SafeDiv(GAa, mu);
        double Aa = SafeDiv(GFa, mu);
        double Vh = SafeDiv(GAh, mu);

        Ah = 1 + shrink * (Ah - 1);
        Va = 1 + shrink * (Va - 1);
        Aa = 1 + shrink * (Aa - 1);
        Vh = 1 + shrink * (Vh - 1);

        double lamH = mu * Ah * Va * hfa;
        double lamA = mu * Aa * Vh;

        int n = Math.Min(s.Home.Matches, s.Away.Matches);
        double w = Math.Min(2.0, Math.Sqrt(Math.Max(0, n)));

        return (lamH, lamA, w);

        static double SafeDiv(double a, double b) => b <= 0 ? 1.0 : Math.Max(0.2, Math.Min(5.0, a / b));
    }

    private static double OverFromLambda(double lambdaTotal, double line)
    {
        int k = (int)Math.Floor(line);              // 1.5→1, 2.5→2, 3.5→3
        double lam = Math.Max(1e-6, lambdaTotal);
        double term = Math.Exp(-lam), cdf = term;   // P(X≤0)
        for (int i = 1; i <= k; i++) { term *= lam / i; cdf += term; }
        return Clamp01(1 - cdf);
    }

    private static (double p1, double px, double p2) OneXTwoFromLambdas(double lamH, double lamA, int maxGoals = 10)
    {
        lamH = Math.Max(1e-6, lamH);
        lamA = Math.Max(1e-6, lamA);

        var ph = new double[maxGoals + 1];
        var pa = new double[maxGoals + 1];
        ph[0] = Math.Exp(-lamH); pa[0] = Math.Exp(-lamA);
        for (int i = 1; i <= maxGoals; i++) { ph[i] = ph[i - 1] * lamH / i; pa[i] = pa[i - 1] * lamA / i; }

        double pHome = 0, pDraw = 0, pAway = 0;
        for (int h = 0; h <= maxGoals; h++)
            for (int a = 0; a <= maxGoals; a++)
            {
                double p = ph[h] * pa[a];
                if (h > a) pHome += p; else if (h == a) pDraw += p; else pAway += p;
            }
        return (Clamp01(pHome), Clamp01(pDraw), Clamp01(pAway));
    }
    // ====================== Double Chance 12 reduction filters ======================
    // Filter 1: px-threshold when draw is meaningful but <= away; reduce 12 by 1–10% in [0.20,0.25]
    private static double Dc12FilterPx(double px, double p2)
    {
        if (px < 0.20 || px > p2 + 1e-12) return 0.0;
        var frac = (px - 0.20) / 0.05;               // 0 at 0.20 -> 1 at 0.25
        var dec = 0.10 * Math.Clamp(frac, 0.0, 1.0); // up to 10%
        if (dec < 0.01 && px >= 0.20) dec = 0.01;    // min 1% if condition holds
        return dec;
    }

    // Filter 2: home dominance curve (matches: diff=0.21 -> 7.25%, diff=0.25 -> 10%)
    private static double Dc12FilterHomeDom(double p1, double p2)
    {
        if (p1 <= p2) return 0.0;
        var diff = p1 - p2;
        const double alpha = 1.846;
        var dec = 0.10 * Math.Pow(Math.Clamp(diff / 0.25, 0.0, 1.0), alpha);
        return Math.Clamp(dec, 0.0, 0.10);
    }

    // Filter 3: away dominance (mirrored curve)
    private static double Dc12FilterAwayDom(double p1, double p2)
    {
        if (p2 <= p1) return 0.0;
        var diff = p2 - p1;
        const double alpha = 1.846;
        var dec = 0.10 * Math.Pow(Math.Clamp(diff / 0.25, 0.0, 1.0), alpha);
        return Math.Clamp(dec, 0.0, 0.10);
    }
}

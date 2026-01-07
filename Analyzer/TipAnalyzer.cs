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
    public static (List<ProposedResult> Results, int H2HEffectiveMatches) Analyze(
                        DetailsItemDto d,
                        string homeName,
                        string awayName,
                        string? proposedCode,
                        double? homeElo,
                        double? awayElo)
    {
        var list = Analyze(d, homeName, awayName, homeElo, awayElo); // core calc

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
        var idx = list.Results
            .Select((r, i) => (r.Code.ToUpperInvariant(), i))
            .ToDictionary(t => t.Item1, t => t.i);

        // convenience getter/setter
        double Get(string code) => idx.TryGetValue(code.ToUpperInvariant(), out var i) ? list.Results[i].Probability : double.NaN;
        void Set(string code, double p)
        {
            if (idx.TryGetValue(code.ToUpperInvariant(), out var i))
                list.Results[i] = new ProposedResult(list.Results[i].Code, Clamp01(p));
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
                bool isHTOver = tip.StartsWith("HT-O");
                bool isHTUnder = tip.StartsWith("HT-U");
        
                string? complement = null;
                if (isOver) complement = tip.Replace("O ", "U ");
                else if (isUnder) complement = tip.Replace("U ", "O ");
                else if (isHTOver) complement = tip.Replace("HT-O", "HT-U");
                else if (isHTUnder) complement = tip.Replace("HT-U", "HT-O");
        
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

    public static (List<ProposedResult> Results, int H2HEffectiveMatches) Analyze(
        DetailsItemDto d,
        string homeName,
        string awayName,
        double? homeElo,
        double? awayElo)
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
        // right after: var h2h = ComputeH2H(d, homeName, awayName);
        int nMass = (int)Math.Round(h2h.MassH2H);
        double wH2H = nMass <= 0 ? 0.0 : wSqrt(nMass);
        double p1H=double.NaN, pxH=double.NaN, p2H=double.NaN;
        double htsH=double.NaN, gtsH=double.NaN, bttsH=double.NaN;
        double o15H=double.NaN, o25H=double.NaN, o35H=double.NaN;
        
        if (!double.IsNaN(h2h.LamH2H) && !double.IsNaN(h2h.LamA2H))
        {
            (p1H, pxH, p2H) = OneXTwoFromLambdas(h2h.LamH2H, h2h.LamA2H);
            htsH = 1.0 - Math.Exp(-h2h.LamH2H);
            gtsH = 1.0 - Math.Exp(-h2h.LamA2H);
            bttsH = htsH * gtsH;
        
            double lamT = h2h.LamH2H + h2h.LamA2H;
            o15H = OverFromLambda(lamT, 1.5);
            o25H = OverFromLambda(lamT, 2.5);
            o35H = OverFromLambda(lamT, 3.5);
        }
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
        
        // NEW: hoisted so we can use them later (OU filters + HT proxies)
        double pressureHome = double.NaN, pressureAway = double.NaN;
        double lamHStand = double.NaN, lamAStand = double.NaN;
        
        var stCtx = TryReadStandings(d, homeName, awayName);
        if (stCtx is not null)
        {
            // Pressure: relegation/Europe (top tier guess) scaled by time & gaps
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
        
            // NEW: store for half-time proxies later
            lamHStand = lamH;
            lamAStand = lamA;
        
            wStand = w;
        
            // Turn lambdas into market hints
            o15Stand = OverFromLambda(lamH + lamA, 1.5);
            o25Stand = OverFromLambda(lamH + lamA, 2.5);
            o35Stand = OverFromLambda(lamH + lamA, 3.5);
        
            htsStand = 1 - Math.Exp(-lamH);
            gtsStand = 1 - Math.Exp(-lamA);
            bttsStand = htsStand * gtsStand;
        
            (p1Stand, pxStand, p2Stand) = OneXTwoFromLambdas(lamH, lamA);
            pxStand = Clamp01(pxStand * drawM);
        
            double s1x2 = p1Stand + pxStand + p2Stand;
            if (s1x2 > 1e-9) { p1Stand /= s1x2; pxStand /= s1x2; p2Stand /= s1x2; }
        }

        // --- 2) Derive market probabilities from signals ---------------------
        double p1Elo = double.NaN, pxElo = double.NaN, p2Elo = double.NaN;
        double wElo = 0.0;
        double htsElo = double.NaN, gtsElo = double.NaN;
        double wEloGoals = 0.0;
        double lamHElo = double.NaN, lamAElo = double.NaN;
        double o15Elo = double.NaN, o25Elo = double.NaN, o35Elo = double.NaN;
        double wEloOU = 0.0;
        
        if (homeElo is double he && awayElo is double ae)
        {
            // Keep consistent with EloTo1X2
            const double hfa = 60.0;
            var diff = (he + hfa) - ae;
            var absDiff = Math.Abs(diff);
        
            (p1Elo, pxElo, p2Elo) = EloTo1X2(he, ae);
        
            // --- Evidence already in your analyzer (same scale you blend with) ---
            double evidence =
                  wChart1x2
                + nMass
                + wSqrt(sepHome.N + sepAway.N)
                + wSqrt(factsHome.N + factsAway.N)
                + wStand;
        
            // --- Your requested scarcityBoost steps ---
            double scarcityBoost;
            if (evidence < 5.0) scarcityBoost = 1.65;
            else if (evidence < 10.0) scarcityBoost = 1.45;
            else if (evidence < 15.0) scarcityBoost = 1.35;
            else scarcityBoost = 1.20;
        
            // --- Gap factor: 0 at 0 Elo, 1 at 200 Elo, 2 at 400 Elo (capped) ---
            var gap = Math.Clamp(absDiff / 200.0, 0.0, 2.0);
        
            // --- Elo level factor: depends on average Elo (higher avg -> more trust) ---
            // Tune the range if your dataset differs; these work well for typical club Elo.
            var avgElo = (he + ae) / 2.0;
            var level01 = Math.Clamp((avgElo - 1500.0) / 600.0, 0.0, 1.0); // 1500..2100 -> 0..1
            var levelBoost = 1.0 + 0.60 * level01;                         // 1.00 .. 1.60
        
            // --- Base Elo weight: make it strong, and increase with mismatch ---
            // At gap=0 -> ~3.0 * boosts
            // At gap=1 (200 Elo) -> ~5.0 * boosts
            // At gap=2 (400 Elo) -> ~7.0 * boosts
            var baseW = 3.0 + 2.0 * gap;
        
            wElo = baseW * scarcityBoost * levelBoost;
        
            // If chart 1X2 is missing, Elo becomes even more backbone
            if (wChart1x2 <= 0.0)
                wElo *= 1.25;
        
            // --- Guarantee: Elo is a MAJOR share of total blend weight ---
            // (Set this higher/lower based on how dominant you want Elo to be.)
            // Example: 60% of evidence means Elo often becomes the largest single contributor.
            var targetShare = 0.75;
            wElo = Math.Max(wElo, targetShare * evidence);
        
            // Hard caps (avoid crazy weights)
            wElo = Math.Clamp(wElo, 2.5, 12.0);
            
            //(htsElo, gtsElo) = EloToTeamScores(he, ae);
        
            // Make goals follow Elo strongly, but slightly less than 1X2
            // (If you want: set equal to wElo)
            //wEloGoals = 0.75 * wElo;
            (lamHElo, lamAElo) = EloToLambdas(he, ae);

            htsElo = 1.0 - Math.Exp(-lamHElo);
            gtsElo = 1.0 - Math.Exp(-lamAElo);
            
            var lamTElo = lamHElo + lamAElo;
            o15Elo = OverFromLambda(lamTElo, 1.5);
            o25Elo = OverFromLambda(lamTElo, 2.5);
            o35Elo = OverFromLambda(lamTElo, 3.5);
            
            // weights for goal-derived markets
            wEloGoals = 0.75 * wElo;
            wEloOU    = 0.75 * wElo;
        }
        
        // 1X2 from: charts + H2H outcomes + separate (wins/draws/loss) + facts (wins/draws/loss) + standings hint
        var p1 = Blend(new[]
        {
            C(chart1x2.Home,   w: wChart1x2),
            C(h2h.PHome,       w: nMass),
            C(p1H,             w: 0.8 * wH2H),                 // NEW
            C(Avg( sepHome.WinRate,  sepAway.LossRate ), w: wSqrt(sepHome.N + sepAway.N)),
            C(Avg(factsHome.WinRate, factsAway.LossRate), w: wSqrt(factsHome.N + factsAway.N)),
            C(p1Stand,         w: wStand), // NEW
            C(p1Elo,           w: wElo)  
        });

        var px = Blend(new[]
                {
            C(chart1x2.Draw,   w: wChart1x2),
            C(h2h.PDraw,       w: nMass),
            C(pxH,             w: 0.8 * wH2H),                 // NEW
            C(Avg( sepHome.DrawRate,  sepAway.DrawRate ), w: wSqrt(sepHome.N + sepAway.N)),
            C(Avg(factsHome.DrawRate, factsAway.DrawRate), w: wSqrt(factsHome.N + factsAway.N)),
            C(pxStand,         w: wStand), // NEW
            C(pxElo,           w: wElo)
        });

        var p2 = Blend(new[]
        {
            C(chart1x2.Away,   w: wChart1x2),
            C(h2h.PAway,       w: nMass),
            C(p2H,             w: 0.8 * wH2H),                 // NEW
            C(Avg( sepAway.WinRate,  sepHome.LossRate ), w: wSqrt(sepHome.N + sepAway.N)),
            C(Avg(factsAway.WinRate, factsHome.LossRate), w: wSqrt(factsHome.N + factsAway.N)),
            C(p2Stand,         w: wStand), // NEW
            C(p2Elo,           w: wElo) 
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
            C(h2h.PBTTS,                       w: nMass),
            C(bttsH,                           w: 0.8 * wH2H),     // NEW
            // Independence-ish: P(BTTS) ≈ P(home scores)*P(away scores)
            C(sepHome.ScoreRate * sepAway.ScoreRate,                  w: wSqrt(sepHome.N + sepAway.N)),
            C(factsHome.ScoreChance * factsAway.ScoreChance,          w: 1.5),
            C(factsHome.ConcedeChance * factsAway.ConcedeChance,      w: 1.5),
            C(bttsStand,                                              w: wStand) // NEW
        });

        var ots = Blend(new[]
        {
            C(chartOts,                        w: wChartOts),
            C(h2h.POnlyOne,                    w: nMass),
            // ≈ P(home scores)*(1-P(away scores)) + P(away scores)*(1-P(home scores))
            C(sepHome.ScoreRate*(1-sepAway.ScoreRate) + sepAway.ScoreRate*(1-sepHome.ScoreRate),
              w: wSqrt(sepHome.N + sepAway.N))
            // (You can add a facts-only OTS proxy if you want; left as-is)
        });

        // HTS/GTS with standings hint
        var hts = Blend(new[]
        {
            C(Avg(factsHome.ScoreChance, factsAway.ConcedeChance), w: 2),
            C(h2h.PHomeScored,                                     w: nMass),
            C(htsH,                                                w: 0.8 * wH2H), // NEW
            C(Avg(sepHome.ScoreRate,   sepAway.ConcedeRate),       w: wSqrt(sepHome.N + sepAway.N)),
            C(htsStand,                                            w: wStand), // NEW
            C(htsElo,                                              w: wEloGoals)
        });

        var gts = Blend(new[]
        {
            C(Avg(factsAway.ScoreChance, factsHome.ConcedeChance), w: 2),
            C(h2h.PAwayScored,                                     w: nMass),
            C(gtsH,                                                w: 0.8 * wH2H), // NEW
            C(Avg(sepAway.ScoreRate,   sepHome.ConcedeRate),       w: wSqrt(sepHome.N + sepAway.N)),
            C(gtsStand,                                            w: wStand), // NEW
            C(gtsElo,                                              w: wEloGoals)
        });

        // Over/Under totals: chart + H2H + separate + facts (2.5) + facts-Poisson + standings-Poisson
        var o15 = Blend(new[]
        {
            C(chartOU15,                                     w: wChartOU15),
            C(h2h.POver15,                                   w: nMass),
            C(o15H,                                          w: 0.8 * wH2H),  // NEW
            C(Avg(sepHome.Over15Rate, sepAway.Over15Rate),   w: wSqrt(sepHome.N + sepAway.N)),
            C(o15Facts,                                      w: 1.3),
            C(o15Stand,                                      w: Math.Max(1.0, 0.8*wStand)),
            C(o15Elo,                                        w: wEloOU) // <-- NEW
        });
        var u15 = 1 - o15;
        
        var o25 = Blend(new[]
        {
            C(chartOU25,                                     w: wChartOU25),
            C(h2h.POver25,                                   w: nMass),
            C(o25H,                                          w: 0.8 * wH2H),  // NEW
            C(Avg(sepHome.Over25Rate, sepAway.Over25Rate),   w: wSqrt(sepHome.N + sepAway.N)),
            C(Avg(factsHome.Over25RateFacts, factsAway.Over25RateFacts), w: wSqrt(factsHome.N + factsAway.N)),
            C(o25Facts,                                      w: 1.3),
            C(o25Stand,                                      w: Math.Max(1.0, 0.8*wStand)),
            C(o25Elo,                                        w: wEloOU) // <-- NEW
        });
        var u25 = 1 - o25;
        
        var o35 = Blend(new[]
        {
            C(chartOU35,                                     w: wChartOU35),
            C(h2h.POver35,                                   w: nMass),
            C(o35H,                                          w: 0.8 * wH2H),  // NEW
            C(Avg(sepHome.Over35Rate, sepAway.Over35Rate),   w: wSqrt(sepHome.N + sepAway.N)),
            C(o35Facts,                                      w: 1.3),
            C(o35Stand,                                      w: Math.Max(1.0, 0.8*wStand)),
            C(o35Elo,                                        w: wEloOU) // <-- NEW
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

        // NEW: Low-stakes OU dampening (only when we actually have pressure)
        if (!double.IsNaN(pressureHome) && !double.IsNaN(pressureAway))
        {
            double pressureSum = pressureHome + pressureAway; // 0..2-ish
            if (pressureSum < 0.5)
            {
                // up to -5% when pressureSum -> 0
                double lowStakesFactor = 0.10 * (0.5 - pressureSum);
                o15 = Clamp01(o15 * (1.0 - lowStakesFactor));
                o25 = Clamp01(o25 * (1.0 - lowStakesFactor));
                o35 = Clamp01(o35 * (1.0 - lowStakesFactor));
            }
        }
        
        // NEW: Filter for O1.5 if low BTTS expectation
        if (!double.IsNaN(btts) && btts < 0.40)
        {
            double lowBttsPenalty = 0.08 * (0.40 - btts) / 0.40; // up to -8%
            o15 = Clamp01(o15 * (1.0 - lowBttsPenalty));
        }
        
        // Refresh unders after any OU adjustments (you already do this earlier; this keeps it consistent)
        u15 = 1 - o15; u25 = 1 - o25; u35 = 1 - o35;


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

        // Half-time O/U proxy from full-time lambdas (fallback only)
        double hto15 = double.NaN;//, hto25 = double.NaN, hto35 = double.NaN;
        double htu15 = double.NaN;//, htu25 = double.NaN, htu35 = double.NaN;
        
        // Build a blended full-time total lambda from whatever we have (Standings/Elo/H2H)
        double lamTFull = double.NaN;
        {
            double sw = 0.0, sLam = 0.0;
        
            void AddLam(double lamT, double w)
            {
                if (!double.IsNaN(lamT) && lamT > 0 && w > 0)
                {
                    sLam += lamT * w;
                    sw += w;
                }
            }
        
            // standings lambda total (stored earlier)
            AddLam(lamHStand + lamAStand, wStand);
        
            // elo lambda total (already computed earlier in your Elo block)
            AddLam(lamHElo + lamAElo, wEloOU);
        
            // h2h lambda total (use a slightly reduced weight)
            AddLam(h2h.LamH2H + h2h.LamA2H, 0.8 * wH2H);
        
            if (sw > 0) lamTFull = sLam / sw;
        }
        
        if (!double.IsNaN(lamTFull) && lamTFull > 0)
        {
            // empirical first-half ratio
            double lamTHalf = 0.45 * lamTFull;
        
            hto15 = OverFromLambda(lamTHalf, 1.5);
            //hto25 = OverFromLambda(lamTHalf, 2.5);
            //hto35 = OverFromLambda(lamTHalf, 3.5);
        
            htu15 = 1.0 - hto15;
            //htu25 = 1.0 - hto25;
            //htu35 = 1.0 - hto35;
        }
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
        
            new("BTTS", btts), new("OTS", ots),
            new("H>0.5", hts_disp),  new("A>0.5", gts_disp),
        
            new("O 1.5", o15), new("O 2.5", o25), new("O 3.5", o35),
            new("U 1.5", u15), new("U 2.5", u25), new("U 3.5", u35),
        
            new("HT-O1.5", hto15),
            new("HT-U1.5", htu15 - 0.10),
        };

        // Hide empty markets entirely
        results = results.Where(r => !double.IsNaN(r.Probability)).ToList();

        if (nMass < 0) nMass = 0;

        return (results, nMass);
    }


    // ====================== helpers ======================
    // ============ VIP eligibility + guards ============

    // Read "without goal" for a given team from the "Time of first goal" chart(s)
    private static double ReadNoGoalFromFirstGoalChart(DetailsItemDto d, string teamName)
    {
        if (d?.BarCharts == null) return double.NaN;
    
        bool TitleMatch(string? t)
            => t?.IndexOf("time of first", StringComparison.OrdinalIgnoreCase) >= 0
            || t?.IndexOf("time of first goal", StringComparison.OrdinalIgnoreCase) >= 0;
    
        double? read(string halfId, string item)
        {
            var chart = d.BarCharts.FirstOrDefault(c =>
                TitleMatch(c.Title) &&
                string.Equals(c.HalfContainerId, halfId, StringComparison.OrdinalIgnoreCase));
    
            var it = chart?.Items?.FirstOrDefault(i =>
                string.Equals(i.Name, item, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(i.Name, "without goal", StringComparison.OrdinalIgnoreCase));
    
            return it?.Percentage;
        }
    
        // Try both halves, prefer current home when teamName == home later, but here we just avg both
        var c1 = read("HalfContainer1", "without goal");
        var c2 = read("HalfContainer2", "without goal");
    
        var xs = new[] { c1, c2 }.Where(v => v.HasValue).Select(v => v!.Value).ToArray();
        if (xs.Length == 0) return double.NaN;
        return Math.Clamp(xs.Average() / 100.0, 0.0, 1.0);
    }
    
    // Small weighted mean with NaN-skip
    private static double WeightedMean(double[] ps, double[] ws)
    {
        double sw = 0, sp = 0;
        for (int i = 0; i < ps.Length; i++)
        {
            double p = ps[i], w = ws[i];
            if (!double.IsNaN(p) && w > 0) { sw += w; sp += p * w; }
        }
        return sw <= 0 ? double.NaN : Math.Clamp(sp / sw, 0.0, 1.0);
    }
    
    // Build a fast lookup from Analyze(...) results
    private static Dictionary<string, double> ToMap(List<ProposedResult> results)
        => results.ToDictionary(r => r.Code.ToUpperInvariant(), r => r.Probability);
    
    // Choose the safest high-hit fallback when VIP HTS is blocked
    private static (string code, double p) ChooseSafeFallback(Dictionary<string, double> P)
    {
        // 1) O 1.5 if strong
        if (P.TryGetValue("O 1.5", out var o15) && o15 >= 0.65) return ("O 1.5", o15);
    
        // 2) Best Double Chance
        var dc = new[] { "1X", "X2", "12" }
            .Where(P.ContainsKey)
            .Select(code => (code, P[code]))
            .OrderByDescending(t => t.Item2)
            .FirstOrDefault();
        if (!string.IsNullOrEmpty(dc.code) && dc.Item2 >= 0.70) return dc;
    
        // 3) As a last resort, pick the top probability overall
        var best = P.OrderByDescending(kv => kv.Value).FirstOrDefault();
        return (best.Key ?? "1X", best.Value);
    }
    
    // Eligibility with all guards & coherence cap
    private static bool HtsVipEligible(
        Dictionary<string, double> P,
        double pNoGoalHome,          // from charts (coherence)
        double pHtsPoisBlend         // from H2H/Standings/Elo lambdas
    )
    {
        double Get(string code) => P.TryGetValue(code.ToUpperInvariant(), out var v) ? v : double.NaN;
    
        double pHTS_raw = Get("HTS");
        double pO15     = Get("O 1.5");
        double pBTTS    = Get("BTS");
        double pGTS     = Get("GTS");
        double pU35     = Get("U 3.5");
    
        // 1) Coherence cap against "no goal" signal
        double tau = 0.04; // small slack
        double pHtsCoh = double.IsNaN(pNoGoalHome) ? pHTS_raw : Math.Min(pHTS_raw, 1.0 - pNoGoalHome + tau);
    
        // 2) Blend with Poisson-based estimate (from H2H/Standings/Elo)
        double pHTS = WeightedMean(
            new[] { pHTS_raw, pHtsCoh, pHtsPoisBlend },
            new[] { 1.0,      1.2,     1.0 }  // give small priority to the coherence-capped value
        );
    
        if (double.IsNaN(pHTS)) return false;
    
        // 3) Hard guards
        if (pHTS < 0.70) return false;                 // floor (tune 0.65–0.75)
        if (!double.IsNaN(pO15) && pO15 < 0.60) return false; // environment too low-scoring
        if (!double.IsNaN(pU35) && !double.IsNaN(pBTTS) && pU35 > 0.75 && pBTTS < 0.45) return false;
    
        // Nil-nil trap: any 2 true -> block
        int traps = 0;
        if (!double.IsNaN(pNoGoalHome) && pNoGoalHome >= 0.15) traps++;
        if (!double.IsNaN(pBTTS) && pBTTS <= 0.55) traps++;
        if (!double.IsNaN(pO15) && pO15 <= 0.62) traps++;
        if (traps >= 2) return false;
    
        // Extra confirmation: allow if GTS is reasonably high even when BTTS is modest
        if (!double.IsNaN(pGTS) && pGTS >= 0.60) return true;
    
        return true;
    }
    
    // Build an HTS Poisson hint from whatever lambdas you already computed
    private static double HtsPoisHint(
        double htsH, double htsStand, double htsElo,
        double wH2H, double wStand, double wEloGoals)
    {
        return WeightedMean(
            new[] { htsH, htsStand, htsElo },
            new[] { 0.8 * wH2H, wStand, wEloGoals }
        );
    }
    /*
    // --- read "without goal" from your First-Goal charts (coherence signal)
    private static double ReadNoGoalFromFirstGoalChart(DetailsItemDto d, string teamName)
    {
        if (d?.BarCharts == null) return double.NaN;
    
        bool TitleMatch(string? t) =>
            t?.IndexOf("time of first", StringComparison.OrdinalIgnoreCase) >= 0;
    
        double? read(string halfId)
            => d.BarCharts.FirstOrDefault(c =>
                    TitleMatch(c.Title) &&
                    string.Equals(c.HalfContainerId, halfId, StringComparison.OrdinalIgnoreCase))
                 ?.Items?.FirstOrDefault(i =>
                    string.Equals(i.Name, "without goal", StringComparison.OrdinalIgnoreCase))
                 ?.Percentage;
    
        var vals = new[] { read("HalfContainer1"), read("HalfContainer2") }
                   .Where(v => v.HasValue).Select(v => v!.Value).ToArray();
        if (vals.Length == 0) return double.NaN;
        return Math.Clamp(vals.Average() / 100.0, 0.0, 1.0);
    }
    
    
    private static double WeightedMean(double[] ps, double[] ws)
    {
        double sw = 0, sp = 0;
        for (int i = 0; i < ps.Length; i++)
        {
            var p = ps[i]; var w = ws[i];
            if (!double.IsNaN(p) && w > 0) { sw += w; sp += p * w; }
        }
        return sw <= 0 ? double.NaN : Math.Clamp(sp / sw, 0.0, 1.0);
    }
    */
    public static (string code, double probability, string reason) PickVip(
        DetailsItemDto d,
        string homeName,
        string awayName,
        List<ProposedResult> results,
        double? homeElo,
        double? awayElo)
    {
        // quick lookup
        var P = results.ToDictionary(r => r.Code.ToUpperInvariant(), r => r.Probability);
        double Get(string code) => P.TryGetValue(code.ToUpperInvariant(), out var v) ? v : double.NaN;
    
        // coherence: "no goal" mass for home from First-Goal chart
        double pNoGoalHome = ReadNoGoalFromFirstGoalChart(d, homeName);
    
        // Poisson hint for HTS from H2H lambdas + Elo lambdas (time-decayed H2H already implemented)
        var h2h = ComputeH2H(d, homeName, awayName);             // has LamH2H/LamA2H
        int nMass = (int)Math.Round(h2h.MassH2H);
        double wH2H = nMass <= 0 ? 0.0 : wSqrt(nMass);
    
        double htsH = double.IsNaN(h2h.LamH2H) ? double.NaN : (1 - Math.Exp(-h2h.LamH2H));
    
        double htsElo = double.NaN;
        double wEloGoals = 0.0;
        if (homeElo is double he && awayElo is double ae)
        {
            var (htsE, gtsE) = EloToTeamScores(he, ae); // returns P(home scores), P(away scores)
            htsElo = htsE;
            wEloGoals = 3.0; // solid default weight
        }
    
        double pHtsPois = WeightedMean(
            new[] { htsH, htsElo },
            new[] { 0.8 * wH2H, wEloGoals }
        );
    
        // raw model outputs
        double pHTS  = Get("HTS");
        double pO15  = Get("O 1.5");
        double pBTTS = Get("BTS");
        double pGTS  = Get("GTS");
        double pU35  = Get("U 3.5");
    
        // --- guards ---
        // coherence cap
        double tau = 0.04;
        if (!double.IsNaN(pNoGoalHome)) pHTS = Math.Min(pHTS, 1.0 - pNoGoalHome + tau);
    
        // blend with Poisson hint (keeps spikes in check)
        pHTS = WeightedMean(new[] { pHTS, pHtsPois }, new[] { 1.2, 1.0 });
    
        // floors / confirmations
        bool ok = !double.IsNaN(pHTS) && pHTS >= 0.70
                  && (double.IsNaN(pO15) || pO15 >= 0.60)
                  && !( !double.IsNaN(pU35) && !double.IsNaN(pBTTS) && pU35 > 0.75 && pBTTS < 0.45 );
    
        // nil-nil trap (any 2)
        int traps = 0;
        if (!double.IsNaN(pNoGoalHome) && pNoGoalHome >= 0.15) traps++;
        if (!double.IsNaN(pBTTS) && pBTTS <= 0.55) traps++;
        if (!double.IsNaN(pO15) && pO15 <= 0.62) traps++;
        if (traps >= 2) ok = false;
    
        if (ok) return ("HTS", Get("HTS"), "HTS passed guards (coherence + totals/BTTS + trap).");
    
        // --- fallback: safer high-hit markets ---
        // prefer O1.5 if strong
        if (!double.IsNaN(pO15) && pO15 >= 0.65) return ("O 1.5", pO15, "HTS blocked; fallback O1.5.");
    
        // else best Double Chance >= 0.70
        (string code, double p) dc = new();
        foreach (var code in new[] { "1X", "X2", "12" })
            if (!double.IsNaN(Get(code)) && Get(code) > dc.p) dc = (code, Get(code));
        if (!string.IsNullOrEmpty(dc.code) && dc.p >= 0.70)
            return (dc.code, dc.p, "HTS blocked; fallback Double Chance.");
    
        // else best overall
        var best = results.OrderByDescending(r => r.Probability).First();
        return (best.Code, best.Probability, "HTS blocked; fallback to top model probability.");
    }
    // Public helper: run after Analyze(...) to finalize a VIP suggestion with guards+fallback.
    public static (string code, double probability, string reason) SuggestVip(
        DetailsItemDto d,
        string homeName,
        string awayName,
        List<ProposedResult> results,
        // pass through key internals you already compute in Analyze(...)
        double htsH, double htsStand, double htsElo,
        double wH2H, double wStand, double wEloGoals
    )
    {
        var P = ToMap(results);
    
        // 1) compute "home no-goal" from chart for coherence (if absent, becomes NaN)
        double pNoGoalHome = ReadNoGoalFromFirstGoalChart(d, homeName);
    
        // 2) Poisson blend hint for HTS
        double pHtsPois = HtsPoisHint(htsH, htsStand, htsElo, wH2H, wStand, wEloGoals);
    
        // 3) If caller proposed HTS, check eligibility; else we'll just choose the best safe market
        bool wantHTS = P.ContainsKey("HTS");
    
        if (wantHTS && HtsVipEligible(P, pNoGoalHome, pHtsPois))
        {
            return ("HTS", P["HTS"], "HTS passed all guards (coherence, totals/BTTS, trap check).");
        }
    
        // 4) Fallback if HTS blocked or not proposed
        var fb = ChooseSafeFallback(P);
        string why = wantHTS
            ? "HTS blocked by guards; promoting safer high-hit market."
            : "No VIP preference given; selecting safest high-probability market.";
        return (fb.code, fb.p, why);
    }
    // Keep it near the other helpers
    private static (double lamH, double lamA) EloToLambdas(double homeElo, double awayElo)
    {
        const double hfa = 60.0;
        var diff = (homeElo + hfa) - awayElo;
    
        // Baseline team goal rates (tune later)
        const double baseLamHome = 1.45;
        const double baseLamAway = 1.15;
    
        // Elo -> goals sensitivity (tune 0.45..0.75)
        const double k = 0.60;
    
        // ratio >1 favors home; <1 favors away
        var r = Math.Exp(k * diff / 400.0);
    
        var lamH = Math.Max(1e-6, baseLamHome * r);
        var lamA = Math.Max(1e-6, baseLamAway / r);
    
        return (lamH, lamA);
    }
    private static (double p1, double px, double p2) EloTo1X2(double homeElo, double awayElo)
    {
        const double hfa = 60.0;       // home-field Elo boost
        const double baseDraw = 0.26;  // typical draw rate baseline
        const double drawScale = 250.0;
    
        var diff = (homeElo + hfa) - awayElo;
    
        var pHomeNoDraw = 1.0 / (1.0 + Math.Pow(10.0, -diff / 400.0));
        var pDraw = baseDraw * Math.Exp(-Math.Abs(diff) / drawScale);
    
        var p1 = (1.0 - pDraw) * pHomeNoDraw;
        var p2 = (1.0 - pDraw) * (1.0 - pHomeNoDraw);
    
        return (p1, pDraw, p2);
    }
    
    private static (double hts, double gts) EloToTeamScores(double homeElo, double awayElo)
    {
        const double hfa = 60.0;
    
        // diff in Elo points (same convention as EloTo1X2)
        var diff = (homeElo + hfa) - awayElo;
    
        // Baseline xG-ish rates (tune later; these are reasonable starters)
        const double baseLamHome = 1.45;
        const double baseLamAway = 1.15;
    
        // How strongly Elo moves expected goals (tune 0.45..0.75)
        const double k = 0.60;
    
        // Convert Elo diff to multiplicative goal-strength ratio
        // +diff => home λ up, away λ down
        var r = Math.Exp(k * diff / 400.0);
    
        var lamH = baseLamHome * r;
        var lamA = baseLamAway / r;
    
        // P(score >= 1) = 1 - exp(-λ)
        double pH = 1.0 - Math.Exp(-lamH);
        double pA = 1.0 - Math.Exp(-lamA);
    
        // keep it sane
        pH = Clamp01(pH);
        pA = Clamp01(pA);
    
        return (pH, pA);
    }

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

    
    private sealed record H2HStats(
        int NH2H,
        double PHome, double PDraw, double PAway,
        double POver15, double POver25, double POver35,
        double PBTTS, double POnlyOne,
        double PHomeScored, double PAwayScored,
        // Poisson from H2H (time-decayed)
        double LamH2H, double LamA2H,
        // NEW: total decayed sample mass (W) before rounding
        double MassH2H = 0.0,
        // NEW: diagnostics – how many matches were ignored due to hard cutoff
        int IgnoredOld = 0,
        // NEW: diagnostics – how many duplicate rows were skipped
        int IgnoredDuplicates = 0
    );

    // ---------- H2H AGGREGATION (revised) ----------
    private static H2HStats ComputeH2H(DetailsItemDto d, string home, string away)
    {
        var matches = d?.MatchDataBetween?.Matches ?? new List<MatchBetweenItemDto>();
    
        // --- decay & policy parameters (tune) ---
        const double HALF_LIFE_DAYS = 365.0;     // halves every ~1 year
        const double MIN_W = 0.05;               // floor weight for very old games
        const int HARD_CUTOFF_DAYS = 5475;       // ignore > 10 years
        const double UNKNOWN_DATE_WEIGHT = MIN_W; // conservative for undated matches
    
        DateTime today = DateTime.UtcNow;
    
        // weighted tallies
        double W = 0, wHome = 0, wDraw = 0, wAway = 0,
               wOv15 = 0, wOv25 = 0, wOv35 = 0,
               wBTTS = 0, wOnlyOne = 0, wHSc = 0, wASc = 0;
    
        // weighted goals (relative to *current* home/away)
        double gH = 0.0, gA = 0.0;
    
        // simple duplicate guard: (date|host|guest|hg-gg)
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        int ignoredOld = 0;
        int ignoredDups = 0;
    
        foreach (var m in matches)
        {
            if (!TryInt(m.HostGoals, out var hg) || !TryInt(m.GuestGoals, out var gg)) continue;
    
            string host = m.HostTeam ?? "", guest = m.GuestTeam ?? "";
            if (!(SameTeam(host, home) && SameTeam(guest, away)) &&
                !(SameTeam(host, away) && SameTeam(guest, home))) continue;
    
            // Build a duplicate key (date if available, else "nodate")
            string dateKey = TryReadMatchDate(m, out var md) ? md.ToString("yyyy-MM-dd") : "nodate";
            string dupKey = $"{dateKey}|{host}|{guest}|{hg}-{gg}";
            if (!seen.Add(dupKey))
            {
                ignoredDups++;
                continue; // skip exact duplicates
            }
                
    
            // --- time decay weight ---
            double w;
            if (TryReadMatchDate(m, out md))
            {
                var ageDays = Math.Max(0.0, (today - md).TotalDays);
    
                // hard cutoff for very old history
                if (ageDays > HARD_CUTOFF_DAYS)
                {
                    ignoredOld++;
                    continue;
                }
    
                w = Math.Exp(-Math.Log(2.0) * ageDays / Math.Max(1.0, HALF_LIFE_DAYS));
                if (w < MIN_W) w = MIN_W;
            }
            else
            {
                // unknown date -> conservative small weight
                w = UNKNOWN_DATE_WEIGHT;
            }
    
            // normalize goal orientation to CURRENT home/away
            bool hostIsCurrentHome = SameTeam(host, home);
            int relHomeGoals = hostIsCurrentHome ? hg : gg;
            int relAwayGoals = hostIsCurrentHome ? gg : hg;
    
            W += w;
    
            // outcomes
            if (hg > gg)      AddWin(ref wHome, ref wAway, hostIsCurrentHome,  w);
            else if (hg < gg) AddWin(ref wHome, ref wAway, !hostIsCurrentHome, w);
            else              wDraw += w;
    
            // totals (strict > matches O1.5/O2.5/O3.5)
            int total = hg + gg;
            if (total > 1) wOv15 += w;
            if (total > 2) wOv25 += w;
            if (total > 3) wOv35 += w;
    
            // scoring flags (relative)
            if (relHomeGoals > 0) wHSc += w;
            if (relAwayGoals > 0) wASc += w;
    
            if (hg > 0 && gg > 0) wBTTS += w;
            if ((hg > 0) ^ (gg > 0)) wOnlyOne += w;
    
            // Poisson lambdas from weighted goals
            gH += w * relHomeGoals;
            gA += w * relAwayGoals;
        }
    
        double ToP(double x) => W <= 1e-9 ? double.NaN : x / W;
    
        // Effective N becomes the decayed sample mass W (rounded)
        int nEff = (int)Math.Round(W);
    
        double lamH = (W <= 1e-9) ? double.NaN : Math.Max(1e-6, gH / W);
        double lamA = (W <= 1e-9) ? double.NaN : Math.Max(1e-6, gA / W);
    
        // NOTE: Add 'MassH2H' (double) and optionally 'IgnoredOld' (int) to H2HStats
        return new H2HStats(
            NH2H: nEff,
            PHome: ToP(wHome), PDraw: ToP(wDraw), PAway: ToP(wAway),
            POver15: ToP(wOv15), POver25: ToP(wOv25), POver35: ToP(wOv35),
            PBTTS: ToP(wBTTS), POnlyOne: ToP(wOnlyOne),
            PHomeScored: ToP(wHSc), PAwayScored: ToP(wASc),
            LamH2H: lamH, LamA2H: lamA,
            MassH2H: W,
            IgnoredOld: ignoredOld, // optional: remove if not desired
            IgnoredDuplicates: ignoredDups
        );
    }

    /*
    private sealed record H2HStats(
        int NH2H,
        double PHome, double PDraw, double PAway,
        double POver15, double POver25, double POver35,
        double PBTTS, double POnlyOne,
        double PHomeScored, double PAwayScored,
        // NEW: Poisson from H2H (time-decayed)
        double LamH2H, double LamA2H
    );
    // ---------- H2H AGGREGATION ----------
    private static H2HStats ComputeH2H(DetailsItemDto d, string home, string away)
    {
        var matches = d?.MatchDataBetween?.Matches ?? new List<MatchBetweenItemDto>();
    
        // --- decay parameters (tune) ---
        const double HALF_LIFE_DAYS = 365.0;     // every ~1 year halves the impact
        const double MIN_W = 0.10;               // floor weight so very old games aren’t zeroed
        DateTime today = DateTime.UtcNow;
    
        // weighted tallies
        double W = 0, wHome = 0, wDraw = 0, wAway = 0,
               wOv15 = 0, wOv25 = 0, wOv35 = 0,
               wBTTS = 0, wOnlyOne = 0, wHSc = 0, wASc = 0;
    
        // weighted goals (relative to *current* home/away)
        double gH = 0.0, gA = 0.0;
    
        foreach (var m in matches)
        {
            if (!TryInt(m.HostGoals, out var hg) || !TryInt(m.GuestGoals, out var gg)) continue;
            string host = m.HostTeam ?? "", guest = m.GuestTeam ?? "";
            if (!(SameTeam(host, home) && SameTeam(guest, away)) &&
                !(SameTeam(host, away) && SameTeam(guest, home))) continue;
    
            // --- time decay weight ---
            double w = 1.0;
            if (TryReadMatchDate(m, out var md))
            {
                var ageDays = Math.Max(0.0, (today - md).TotalDays);
                w = Math.Exp(-Math.Log(2.0) * ageDays / Math.Max(1.0, HALF_LIFE_DAYS));
                if (w < MIN_W) w = MIN_W;
            }
    
            // normalize goal orientation to CURRENT home/away
            int relHomeGoals = SameTeam(host, home) ? hg : gg;
            int relAwayGoals = SameTeam(host, home) ? gg : hg;
    
            W += w;
    
            // outcomes
            if (hg > gg)      AddWin(ref wHome, ref wAway, SameTeam(host, home),  w);
            else if (hg < gg) AddWin(ref wHome, ref wAway, SameTeam(guest, home), w);
            else              wDraw += w;
    
            // totals
            int total = hg + gg;
            if (total > 1) wOv15 += w;
            if (total > 2) wOv25 += w;
            if (total > 3) wOv35 += w;
    
            // scoring flags (relative)
            if (relHomeGoals > 0) wHSc += w;
            if (relAwayGoals > 0) wASc += w;
    
            if (hg > 0 && gg > 0) wBTTS += w;
            if ((hg > 0) ^ (gg > 0)) wOnlyOne += w;
    
            // Poisson lambdas from weighted goals
            gH += w * relHomeGoals;
            gA += w * relAwayGoals;
        }
    
        double toP(double x) => W <= 1e-9 ? double.NaN : x / W;
    
        // Effective N becomes the decayed sample mass W (rounded to int to preserve your wSqrt(N) API)
        int nEff = (int)Math.Round(W);
    
        double lamH = (W <= 1e-9) ? double.NaN : Math.Max(1e-6, gH / W);
        double lamA = (W <= 1e-9) ? double.NaN : Math.Max(1e-6, gA / W);
    
        return new H2HStats(
            NH2H: nEff,
            PHome: toP(wHome), PDraw: toP(wDraw), PAway: toP(wAway),
            POver15: toP(wOv15), POver25: toP(wOv25), POver35: toP(wOv35),
            PBTTS: toP(wBTTS), POnlyOne: toP(wOnlyOne),
            PHomeScored: toP(wHSc), PAwayScored: toP(wASc),
            LamH2H: lamH, LamA2H: lamA
        );
    }
    */
    static void AddWin(ref double wHome, ref double wAway, bool isHomeWin, double w)
    {
        if (isHomeWin) wHome += w; else wAway += w;
    }
    
    // helper: try to read a date from common fields via your reflection helpers
    private static bool TryReadMatchDate(object m, out DateTime when)
    {
        static bool TryParse(object? o, out DateTime dt)
            => DateTime.TryParse(Convert.ToString(o), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out dt);
    
        foreach (var name in new[] { "MatchDate", "Date", "UtcDate", "Kickoff", "StartTime" })
            if (TryParse(GetProp(m, name), out when)) return true;
    
        when = default;
        return false;
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

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

    // Constants
    private const double Tau1X2 = 0.45;
    private const double TauSolo = 0.35;
    private const double Epsilon = 1e-9;
    private const double ShrinkFactor = 0.70;
    private const double Hfa = 1.10;
    private const double GammaPoisson = 0.90;
    private const double BaseBoostMotivation = 0.10;
    private const double MaxDrawCut = 0.10;

    public static List<ProposedResult> Analyze(
        DetailsItemDto d,
        string homeName,
        string awayName,
        string? proposedCode = null)
    {
        var list = ComputeCoreProbabilities(d, homeName, awayName);

        if (string.IsNullOrWhiteSpace(proposedCode))
            return list;

        // Helpers
        static double Clamp01(double x) => x < 0 ? 0 : (x > 1 ? 1 : x);
        static double Sigmoid(double z) => 1.0 / (1.0 + Math.Exp(-z));
        static double Logit(double p)
        {
            p = Math.Min(1 - Epsilon, Math.Max(Epsilon, p));
            return Math.Log(p / (1 - p));
        }
        static double LogitBump(double p, double tau, double lo = 0.05, double hi = 0.95)
        {
            if (double.IsNaN(p)) return p;
            var pp = Sigmoid(Logit(p) + tau);
            return Math.Min(hi, Math.Max(lo, pp));
        }

        string tip = proposedCode.Trim().ToUpperInvariant();

        var idx = list
            .Select((r, i) => (r.Code.ToUpperInvariant(), i))
            .ToDictionary(t => t.Item1, t => t.i);

        double Get(string code) => idx.TryGetValue(code.ToUpperInvariant(), out var i) ? list[i].Probability : double.NaN;
        void Set(string code, double p)
        {
            if (idx.TryGetValue(code.ToUpperInvariant(), out var i))
                list[i] = new ProposedResult(list[i].Code, Clamp01(p));
        }

        double p1 = Get("1"), px = Get("X"), p2 = Get("2");

        // Single outcome (1, X, 2)
        if (tip is "1" or "X" or "2")
        {
            if (!double.IsNaN(p1 + px + p2) && p1 + px + p2 > Epsilon)
            {
                if (tip == "1") p1 = LogitBump(p1, Tau1X2);
                else if (tip == "X") px = LogitBump(px, Tau1X2);
                else p2 = LogitBump(p2, Tau1X2);

                double s = p1 + px + p2;
                if (s > Epsilon) { p1 /= s; px /= s; p2 /= s; }

                Set("1", p1); Set("X", px); Set("2", p2);

                var (p1xNew, px2New, p12New) = BuildDoubleChance(p1, px, p2);
                Set("1X", p1xNew);
                Set("X2", px2New);
                Set("12", ApplyDc12Filters(p12New, px, p1, p2));
            }
            return list;
        }

        // Double chance (1X, X2, 12) – NEW: proper bumping support
        bool isDoubleChance = tip is "1X" or "X2" or "12";
        if (isDoubleChance)
        {
            double pDc = Get(tip);
            if (!double.IsNaN(pDc))
            {
                pDc = LogitBump(pDc, Tau1X2);
                double pComplement = Clamp01(1.0 - pDc);

                if (tip == "1X")
                {
                    Set("1X", pDc);
                    Set("2", pComplement);

                    double ratioSum = p1 + px;
                    if (ratioSum > Epsilon)
                    {
                        p1 = p1 / ratioSum * pDc;
                        px = px / ratioSum * pDc;
                    }
                    Set("1", p1); Set("X", px);

                    var (_, px2New, p12New) = BuildDoubleChance(p1, px, pComplement);
                    Set("X2", px2New);
                    Set("12", ApplyDc12Filters(p12New, px, p1, pComplement));
                }
                else if (tip == "X2")
                {
                    Set("X2", pDc);
                    Set("1", pComplement);

                    double ratioSum = px + p2;
                    if (ratioSum > Epsilon)
                    {
                        px = px / ratioSum * pDc;
                        p2 = p2 / ratioSum * pDc;
                    }
                    Set("X", px); Set("2", p2);

                    var (p1xNew, _, p12New) = BuildDoubleChance(pComplement, px, p2);
                    Set("1X", p1xNew);
                    Set("12", ApplyDc12Filters(p12New, px, pComplement, p2));
                }
                else // "12"
                {
                    Set("12", pDc);
                    Set("X", pComplement);

                    double ratioSum = p1 + p2;
                    if (ratioSum > Epsilon)
                    {
                        p1 = p1 / ratioSum * pDc;
                        p2 = p2 / ratioSum * pDc;
                    }
                    Set("1", p1); Set("2", p2);

                    var (p1xNew, px2New, _) = BuildDoubleChance(p1, pComplement, p2);
                    Set("1X", p1xNew);
                    Set("X2", px2New);

                    // Re-apply filters after bump
                    Set("12", ApplyDc12Filters(pDc, pComplement, p1, p2));
                }
            }
            return list;
        }

        // Solo markets (BTS, O/U, etc.)
        if (idx.ContainsKey(tip))
        {
            double p = Get(tip);
            if (!double.IsNaN(p))
            {
                p = LogitBump(p, TauSolo);
                Set(tip, p);

                string? complement = GetComplementCode(tip);
                if (complement != null && idx.ContainsKey(complement))
                {
                    Set(complement, Clamp01(1.0 - p));
                }
            }
        }

        return list;
    }

    private static List<ProposedResult> ComputeCoreProbabilities(DetailsItemDto d, string homeName, string awayName)
    {
        // Signals
        var (chart1x2, wChart1x2) = Read1X2FromCharts(d, homeName, awayName);
        double chartBtts = ReadTeamToScore(d, "both") / 100.0;
        double wChartBtts = double.IsNaN(chartBtts) ? 0.0 : 2.0;
        double chartOts = ReadTeamToScore(d, "only one") / 100.0;
        double wChartOts = double.IsNaN(chartOts) ? 0.0 : 2.0;

        double chartOU15 = ReadOverUnder(d, 1.5, true) / 100.0;
        double wChartOU15 = double.IsNaN(chartOU15) ? 0.0 : 2.0;
        double chartOU25 = ReadOverUnder(d, 2.5, true) / 100.0;
        double wChartOU25 = double.IsNaN(chartOU25) ? 0.0 : 2.0;
        double chartOU35 = ReadOverUnder(d, 3.5, true) / 100.0;
        double wChartOU35 = double.IsNaN(chartOU35) ? 0.0 : 2.0;

        var h2h = ComputeH2H(d, homeName, awayName);
        var sepHome = ComputeTeamRatesFromSeparate(d, homeName);
        var sepAway = ComputeTeamRatesFromSeparate(d, awayName);
        var factsHome = ComputeTeamRatesFromFacts(d, homeName);
        var factsAway = ComputeTeamRatesFromFacts(d, awayName);

        double o15Facts = FactsOver(factsHome.ScoreChance, factsAway.ConcedeChance,
                                    factsAway.ScoreChance, factsHome.ConcedeChance, 1.5, GammaPoisson);
        double o25Facts = FactsOver(factsHome.ScoreChance, factsAway.ConcedeChance,
                                    factsAway.ScoreChance, factsHome.ConcedeChance, 2.5, GammaPoisson);
        double o35Facts = FactsOver(factsHome.ScoreChance, factsAway.ConcedeChance,
                                    factsAway.ScoreChance, factsHome.ConcedeChance, 3.5, GammaPoisson);

        // Standings
        var stCtx = TryReadStandings(d, homeName, awayName);
        double wStand = 0;
        double o15Stand = double.NaN, o25Stand = double.NaN, o35Stand = double.NaN;
        double htsStand = double.NaN, gtsStand = double.NaN, bttsStand = double.NaN;
        double p1Stand = double.NaN, pxStand = double.NaN, p2Stand = double.NaN;

        if (stCtx is not null)
        {
            double pressureHome = PressureScore(stCtx.Home.Position, stCtx.Home.Points, stCtx.Home.Matches,
                                                GuessRules(stCtx.Rows.Count, true), true, safeRank: Math.Max(1, stCtx.Rows.Count - 3),
                                                targetPts: stCtx.Home.Points, seasonMatches: 38);
            double pressureAway = PressureScore(stCtx.Away.Position, stCtx.Away.Points, stCtx.Away.Matches,
                                                GuessRules(stCtx.Rows.Count, true), true, safeRank: Math.Max(1, stCtx.Rows.Count - 3),
                                                targetPts: stCtx.Away.Points, seasonMatches: 38);

            var (lamH0, lamA0, w) = LambdasFromStandings(stCtx, Hfa, ShrinkFactor);
            var (attH, attA, drawM) = MotivationMultipliers(pressureHome, pressureAway);
            double lamH = lamH0 * attH;
            double lamA = lamA0 * attA;
            wStand = w;

            o15Stand = OverFromLambda(lamH + lamA, 1.5);
            o25Stand = OverFromLambda(lamH + lamA, 2.5);
            o35Stand = OverFromLambda(lamH + lamA, 3.5);

            htsStand = 1 - Math.Exp(-lamH);
            gtsStand = 1 - Math.Exp(-lamA);
            bttsStand = htsStand * gtsStand;

            (p1Stand, pxStand, p2Stand) = OneXTwoFromLambdas(lamH, lamA);
            pxStand = Clamp01(pxStand * drawM);

            double s1x2 = p1Stand + pxStand + p2Stand;
            if (s1x2 > Epsilon) { p1Stand /= s1x2; pxStand /= s1x2; p2Stand /= s1x2; }
        }

        // 1X2 blending
        double p1 = Blend(new[]
        {
            C(chart1x2.Home, wChart1x2),
            C(h2h.PHome, wSqrt(h2h.NH2H)),
            C(Avg(sepHome.WinRate, sepAway.LossRate), wSqrt(sepHome.N + sepAway.N)),
            C(Avg(factsHome.WinRate, factsAway.LossRate), wSqrt(factsHome.N + factsAway.N)),
            C(p1Stand, wStand)
        });

        double px = Blend(new[]
        {
            C(chart1x2.Draw, wChart1x2),
            C(h2h.PDraw, wSqrt(h2h.NH2H)),
            C(Avg(sepHome.DrawRate, sepAway.DrawRate), wSqrt(sepHome.N + sepAway.N)),
            C(Avg(factsHome.DrawRate, factsAway.DrawRate), wSqrt(factsHome.N + factsAway.N)),
            C(pxStand, wStand)
        });

        double p2 = Blend(new[]
        {
            C(chart1x2.Away, wChart1x2),
            C(h2h.PAway, wSqrt(h2h.NH2H)),
            C(Avg(sepAway.WinRate, sepHome.LossRate), wSqrt(sepHome.N + sepAway.N)),
            C(Avg(factsAway.WinRate, factsHome.LossRate), wSqrt(factsHome.N + factsAway.N)),
            C(p2Stand, wStand)
        });

        (p1, px, p2) = Normalize1X2(p1, px, p2);

        var (p1x, px2, p12Raw) = BuildDoubleChance(p1, px, p2);
        double p12 = ApplyDc12Filters(p12Raw, px, p1, p2);

        // Other markets
        double btts = Blend(new[]
        {
            C(chartBtts, wChartBtts),
            C(h2h.PBTTS, wSqrt(h2h.NH2H)),
            C(sepHome.ScoreRate * sepAway.ScoreRate, wSqrt(sepHome.N + sepAway.N)),
            C(factsHome.ScoreChance * factsAway.ScoreChance, 1.5),
            C(factsHome.ConcedeChance * factsAway.ConcedeChance, 1.5),
            C(bttsStand, wStand)
        });

        double ots = Blend(new[]
        {
            C(chartOts, wChartOts),
            C(h2h.POnlyOne, wSqrt(h2h.NH2H)),
            C(sepHome.ScoreRate * (1 - sepAway.ScoreRate) + sepAway.ScoreRate * (1 - sepHome.ScoreRate), wSqrt(sepHome.N + sepAway.N))
        });

        double hts = Blend(new[]
        {
            C(Avg(factsHome.ScoreChance, factsAway.ConcedeChance), 2),
            C(h2h.PHomeScored, wSqrt(h2h.NH2H)),
            C(Avg(sepHome.ScoreRate, sepAway.ConcedeRate), wSqrt(sepHome.N + sepAway.N)),
            C(htsStand, wStand)
        });

        double gts = Blend(new[]
        {
            C(Avg(factsAway.ScoreChance, factsHome.ConcedeChance), 2),
            C(h2h.PAwayScored, wSqrt(h2h.NH2H)),
            C(Avg(sepAway.ScoreRate, sepHome.ConcedeRate), wSqrt(sepHome.N + sepAway.N)),
            C(gtsStand, wStand)
        });

        double o15 = Blend(new[]
        {
            C(chartOU15, wChartOU15),
            C(h2h.Over15Rate, wSqrt(h2h.NH2H)),
            C(Avg(sepHome.Over15Rate, sepAway.Over15Rate), wSqrt(sepHome.N + sepAway.N)),
            C(o15Facts, 1.3),
            C(o15Stand, wStand)
        });

        double o25 = Blend(new[]
        {
            C(chartOU25, wChartOU25),
            C(h2h.Over25Rate, wSqrt(h2h.NH2H)),
            C(Avg(sepHome.Over25Rate, sepAway.Over25Rate), wSqrt(sepHome.N + sepAway.N)),
            C(Avg(factsHome.Over25RateFacts, factsAway.Over25RateFacts), 1.0),
            C(o25Facts, 1.3),
            C(o25Stand, wStand)
        });

        double o35 = Blend(new[]
        {
            C(chartOU35, wChartOU35),
            C(h2h.Over35Rate, wSqrt(h2h.NH2H)),
            C(Avg(sepHome.Over35Rate, sepAway.Over35Rate), wSqrt(sepHome.N + sepAway.N)),
            C(o35Facts, 1.3),
            C(o35Stand, wStand)
        });

        var results = new List<ProposedResult>();
        void Add(string code, double prob) { if (!double.IsNaN(prob)) results.Add(new ProposedResult(code, Clamp01(prob))); }

        Add("1", p1);
        Add("X", px);
        Add("2", p2);
        Add("1X", p1x);
        Add("X2", px2);
        Add("12", p12);
        Add("BTS", btts);
        Add("OTS", ots);
        Add("HTS", hts);
        Add("GTS", gts);
        Add("O 1.5", o15);
        Add("U 1.5", 1 - o15);
        Add("O 2.5", o25);
        Add("U 2.5", 1 - o25);
        Add("O 3.5", o35);
        Add("U 3.5", 1 - o35);

        return results.OrderByDescending(r => r.Probability).ToList();
    }

    // Helper methods (unchanged or slightly improved)
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

    private static double PressureScore(int teamRank, int teamPts, int matchesPlayed,
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
    private static (double attH, double attA, double drawM) MotivationMultipliers(double pH, double pA) => (1 + BaseBoostMotivation * pH, 1 + BaseBoostMotivation * pA, 1 - MaxDrawCut * Math.Max(pH, pA));
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


    private static (double p1x, double px2, double p12) BuildDoubleChance(double p1, double px, double p2) =>
        (Clamp01(p1 + px), Clamp01(px + p2), Clamp01(p1 + p2));

    private static double ApplyDc12Filters(double p12, double px, double p1, double p2)
    {
        double dec1 = Dc12FilterPx(px, p2);
        double dec2 = Dc12FilterHomeDom(p1, p2);
        double dec3 = Dc12FilterAwayDom(p1, p2);
        return Clamp01(p12 * (1 - Math.Clamp(dec1 + dec2 + dec3, 0, 0.20)));
    }

    private static double Dc12FilterPx(double px, double p2) => px < 0.20 || px > p2 + 0.01 ? 0 : Math.Max(0.01, 0.10 * Math.Clamp((px - 0.20) / 0.05, 0, 1));
    private static double Dc12FilterHomeDom(double p1, double p2) => p1 <= p2 ? 0 : 0.10 * Math.Pow(Math.Clamp((p1 - p2) / 0.25, 0, 1), 1.846);
    private static double Dc12FilterAwayDom(double p1, double p2) => p2 <= p1 ? 0 : 0.10 * Math.Pow(Math.Clamp((p2 - p1) / 0.25, 0, 1), 1.846);

    private static string? GetComplementCode(string tip)
    {
        return tip.StartsWith("O ") ? tip.Replace("O ", "U ") :
               tip.StartsWith("U ") ? tip.Replace("U ", "O ") :
               tip.StartsWith("HTO") ? tip.Replace("HTO", "HTU") :
               tip.StartsWith("HTU") ? tip.Replace("HTU", "HTO") : null;
    }

    private static double wSqrt(double n) => Math.Sqrt(Math.Max(0, n));
    private static (double p, double w) C(double p, double w) => (p, w);
    private static double Avg(double a, double b) => double.IsNaN(a) ? b : double.IsNaN(b) ? a : (a + b) / 2;
    private static double Blend(params (double p, double w)[] items)
    {
        double num = 0, den = 0;
        foreach (var (p, w) in items)
        {
            if (!double.IsNaN(p) && w > 0) { num += p * w; den += w; }
        }
        return den > 0 ? num / den : double.NaN;
    }

    private static (double p1, double px, double p2) Normalize1X2(double p1, double px, double p2)
    {
        double sum = p1 + px + p2;
        if (double.IsNaN(sum) || sum < Epsilon) return (double.NaN, double.NaN, double.NaN);
        return (p1 / sum, px / sum, p2 / sum);
    }

    // Placeholder records (replace with your actual definitions)
    private sealed record H2HStats(int NH2H, double PHome, double PAway, double PDraw, double PBTTS, double POnlyOne, double PHomeScored, double PAwayScored,
                                  double Over15Rate, double Over25Rate, double Over35Rate)
    { public H2HStats() : this(0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0) { } }
    private sealed record TeamRates(int N, double WinRate, double DrawRate, double LossRate,
                                    double ScoreRate, double ConcedeRate,
                                    double Over15Rate, double Over25Rate, double Over35Rate,
                                    double Over25RateFacts, double ScoreChance, double ConcedeChance)
    { }
    private sealed record StandingsContext(StandRowProxy Home, StandRowProxy Away, List<StandRowProxy> Rows, double LeagueMu) { }
    private sealed record StandRowProxy(int Position, string TeamName, int Matches, int Points, int GF, int GA) { }
    private sealed record LeagueRules(int TotalTeams, int RelegationSlots, (int from, int to)? PromoPlayoff = null,
                                      int AutoPromoSlots = 0, int EuropeSlots = 0)
    { }
    private static LeagueRules GuessRules(int totalTeams, bool topTier) => new(totalTeams, topTier && totalTeams == 20 ? 3 : Math.Max(2, totalTeams / 10));
}

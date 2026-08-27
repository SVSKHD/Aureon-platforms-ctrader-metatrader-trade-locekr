// =====================================================================
//  AUREON AXIS XAG  -  cTrader cBot
//  XAGUSD session anchor -> pivot -> confirm -> FADE
//
//  MECHANISM (locked; measured on 2026 M1, 116 trades, 73.3% win vs
//             68.7% breakeven, +0.152R/trade, 1.9R max drawdown)
//    gate     prior-14d ADR / AdrRefPips >= AdrFloor, else no trading
//    scale    k = clamp(ratio, DynMin, DynMax) applied to ALL distances
//    anchor   close of the M1 bar at the session anchor minute
//    pivot    first close ThresholdPips*k above the anchor, up-moves
//             only, within PivotWindow minutes. OBSERVATION ONLY.
//    confirm  first close ConfPips*k either side of the pivot, within
//             ConfWindow minutes
//    entry    market, FADING the confirm:
//                 confirm above pivot -> SELL
//                 confirm below pivot -> BUY
//    exit     server-side stop and limit, flatten at session end
//    chips    ONE per session, counted at PLACEMENT
//
//  CLOCK: cTrader Server.Time is UTC. Broker session minutes are EET
//  (GMT+2 winter / GMT+3 summer). Every bar is stamped with the offset
//  in force AT THAT BAR, never the offset now. Do NOT use TimeZoneInfo.
//
//  Read the VALIDATION GATES in the OnStop report before any P/L.
// =====================================================================

using System;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using cAlgo.API;
using cAlgo.API.Internals;

namespace cAlgo.Robots
{
    public enum ClockMode { Fixed, AutoEET }

    [Robot(TimeZone = TimeZones.UTC, AccessRights = AccessRights.FullAccess)]
    public class AureonAxisXag : Robot
    {
        // ------------------------- MECHANISM -------------------------
        [Parameter("Threshold (pips)", Group = "Mechanism", DefaultValue = 500)]
        public double ThresholdPips { get; set; }

        [Parameter("Confirm (pips)", Group = "Mechanism", DefaultValue = 200)]
        public double ConfPips { get; set; }

        [Parameter("Target (pips)", Group = "Mechanism", DefaultValue = 500)]
        public double TargetPips { get; set; }

        [Parameter("Stop (pips)", Group = "Mechanism", DefaultValue = 1000)]
        public double StopPips { get; set; }

        [Parameter("Pivot window (min)", Group = "Mechanism", DefaultValue = 180)]
        public int PivotWindow { get; set; }

        [Parameter("Confirm window (min)", Group = "Mechanism", DefaultValue = 120)]
        public int ConfWindow { get; set; }

        [Parameter("Max pivot displacement (pips, 0=off)", Group = "Mechanism", DefaultValue = 0)]
        public double MaxDispPips { get; set; }

        [Parameter("Two-way pivot", Group = "Mechanism", DefaultValue = false)]
        public bool TwoWay { get; set; }

        [Parameter("Fade the confirm", Group = "Mechanism", DefaultValue = true)]
        public bool Fade { get; set; }

        // ------------------------- REGIME GATE -----------------------
        [Parameter("ADR scaling", Group = "Regime", DefaultValue = true)]
        public bool Dyn { get; set; }

        [Parameter("ADR days", Group = "Regime", DefaultValue = 14)]
        public int AdrDays { get; set; }

        [Parameter("ADR reference (pips)", Group = "Regime", DefaultValue = 4856)]
        public double AdrRefPips { get; set; }

        [Parameter("ADR floor ratio", Group = "Regime", DefaultValue = 0.70)]
        public double AdrFloor { get; set; }

        [Parameter("Dyn min", Group = "Regime", DefaultValue = 0.40)]
        public double DynMin { get; set; }

        [Parameter("Dyn max", Group = "Regime", DefaultValue = 2.50)]
        public double DynMax { get; set; }

        // ------------------------- SESSIONS --------------------------
        [Parameter("Clock mode", Group = "Sessions", DefaultValue = ClockMode.AutoEET)]
        public ClockMode Mode { get; set; }

        [Parameter("Fixed offset (hours)", Group = "Sessions", DefaultValue = 3)]
        public int FixedOffset { get; set; }

        [Parameter("London anchor", Group = "Sessions", DefaultValue = "09:55")]
        public string LondonAnchor { get; set; }

        [Parameter("London end", Group = "Sessions", DefaultValue = "15:20")]
        public string LondonEnd { get; set; }

        [Parameter("NY anchor", Group = "Sessions", DefaultValue = "16:15")]
        public string NyAnchor { get; set; }

        [Parameter("NY end", Group = "Sessions", DefaultValue = "23:50")]
        public string NyEnd { get; set; }

        [Parameter("Trade London", Group = "Sessions", DefaultValue = true)]
        public bool TradeLondon { get; set; }

        [Parameter("Trade NY", Group = "Sessions", DefaultValue = true)]
        public bool TradeNy { get; set; }

        [Parameter("Min life (min)", Group = "Sessions", DefaultValue = 20)]
        public int MinLifeMin { get; set; }

        // ------------------------- SIZING ----------------------------
        [Parameter("Risk %", Group = "Sizing", DefaultValue = 1.0)]
        public double RiskPct { get; set; }

        [Parameter("Compound", Group = "Sizing", DefaultValue = false)]
        public bool Compound { get; set; }

        [Parameter("Base equity", Group = "Sizing", DefaultValue = 10000)]
        public double BaseEquity { get; set; }

        // ------------------------- GUARDS ----------------------------
        [Parameter("Day loss %", Group = "Guards", DefaultValue = 3.0)]
        public double DayLossPct { get; set; }

        [Parameter("Max drawdown %", Group = "Guards", DefaultValue = 8.0)]
        public double MaxDdPct { get; set; }

        [Parameter("Profit target % (0=off)", Group = "Guards", DefaultValue = 0)]
        public double ProfitPct { get; set; }

        [Parameter("Friday flatten", Group = "Guards", DefaultValue = true)]
        public bool FridayFlat { get; set; }

        [Parameter("Friday flatten at", Group = "Guards", DefaultValue = "22:00")]
        public string FridayFlatAt { get; set; }

        // ------------------------- INFRA -----------------------------
        [Parameter("Label", Group = "Infra", DefaultValue = "AUREON_XAG")]
        public string Lbl { get; set; }

        [Parameter("Verbose", Group = "Infra", DefaultValue = true)]
        public bool Verbose { get; set; }

        [Parameter("Discord webhook", Group = "Infra", DefaultValue = "")]
        public string DiscordHook { get; set; }

        // ------------------------- STATE -----------------------------
        private enum St { Idle, Anchored, Pivoted, Done }

        private class Sess
        {
            public string Name;
            public int AnchorMin, EndMin;
            public bool Enabled;
            public St State = St.Idle;
            public DateTime AnchorTime, PivotTime;
            public double Anchor, Pivot;
            public int Chips;
            public int? OwnerId;
        }

        private Sess[] _s;
        private double _pip;
        private DateTime _day = DateTime.MinValue;
        private double _k = 1.0;
        private bool _dayOk;
        private double _dayStartBal, _peakEquity;
        private bool _halted;
        private string _haltReason = "";
        private int _lastOffset = int.MinValue;

        // open position bookkeeping
        private int? _ownerId;
        private int _ownerIdx = -1;
        private double _openSl, _openTp, _openEntry;
        private int _openDir;
        private DateTime _openTime;
        private string _forced = null;

        // diagnostics
        private int _anchored, _pivotedN, _confirmedN, _tradedN, _sessTraded;
        private int _blkAdr, _blkWarm, _blkNoPivot, _blkNoConf,
                    _blkChip, _blkLife, _blkHalt, _blkOvershoot;
        private int _rejected, _belowMin;
        private int _tpN, _slN, _forcedN, _otherN, _wins, _closed;
        private double _pnl, _riskSum, _riskMin = double.MaxValue, _riskMax;
        private double _tpOver, _slOver, _maxNotional, _maxGap;
        private double _lastDayClose;
        private readonly System.Collections.Generic.List<string> _switches
            = new System.Collections.Generic.List<string>();

        private static readonly HttpClient Http = new HttpClient();

        // ------------------------- CLOCK -----------------------------
        private static DateTime LastSunday(int year, int month)
        {
            var d = new DateTime(year, month, DateTime.DaysInMonth(year, month));
            while (d.DayOfWeek != DayOfWeek.Sunday) d = d.AddDays(-1);
            return d;
        }

        private int OffsetAt(DateTime utc)
        {
            if (Mode == ClockMode.Fixed) return FixedOffset;
            DateTime on = LastSunday(utc.Year, 3).AddHours(1);
            DateTime off = LastSunday(utc.Year, 10).AddHours(1);
            return (utc >= on && utc < off) ? 3 : 2;
        }

        // stamp each bar with the offset in force AT THAT BAR, not now
        private DateTime BrokerTime(DateTime utc)
        {
            int off = OffsetAt(utc);
            if (off != _lastOffset)
            {
                if (_lastOffset != int.MinValue)
                {
                    string msg = string.Format("[CLOCK ] DST switch {0}: GMT+{1} -> GMT+{2}",
                        utc.ToString("yyyy-MM-dd HH:mm"), _lastOffset, off);
                    Print(msg);
                    _switches.Add(msg);
                }
                _lastOffset = off;
            }
            return utc.AddHours(off);
        }

        private static int HmToMin(string hm)
        {
            var p = hm.Split(':');
            return int.Parse(p[0]) * 60 + int.Parse(p[1]);
        }

        private static int MinOfDay(DateTime t) { return t.Hour * 60 + t.Minute; }

        private void Say(string m) { if (Verbose) Print(m); }

        // Outbound only. Wall-clock throttle, never Server.Time: a backtest
        // covers months in seconds and would fire thousands of posts a second.
        private DateTime _lastPost = DateTime.MinValue;
        private void Discord(string msg)
        {
            if (RunningMode != RunningMode.RealTime) return;
            if (string.IsNullOrWhiteSpace(DiscordHook)) return;
            if ((DateTime.UtcNow - _lastPost).TotalSeconds < 2) return;
            _lastPost = DateTime.UtcNow;
            string url = DiscordHook.Trim();
            string body = "{\"content\":\"" + msg.Replace("\"", "'") + "\"}";
            Task.Run(async () =>
            {
                try
                {
                    var c = new StringContent(body, Encoding.UTF8, "application/json");
                    var r = await Http.PostAsync(url, c);
                    if (!r.IsSuccessStatusCode)
                    {
                        int code = (int)r.StatusCode;
                        // main-thread only APIs must never be touched from here
                        BeginInvokeOnMainThread(() => Print("[discord] HTTP " + code));
                    }
                }
                catch (Exception ex)
                {
                    var m = ex.Message;
                    BeginInvokeOnMainThread(() => Print("[discord] " + m));
                }
            });
        }

        // ------------------------- ADR -------------------------------
        // prior N daily bars only; today is never included
        private double PriorAdr(DateTime brokerDay)
        {
            var d = MarketData.GetBars(TimeFrame.Daily);
            if (d == null || d.Count < AdrDays + 2) return 0;
            double sum = 0; int n = 0;
            for (int i = d.Count - 2; i >= 0 && n < AdrDays; i--)
            {
                if (d.OpenTimes[i].Date >= brokerDay.Date) continue;
                double r = d.HighPrices[i] - d.LowPrices[i];
                if (r > 0) { sum += r; n++; }
            }
            return n >= 5 ? sum / n : 0;
        }

        // ------------------------- SIZING ----------------------------
        private double VolumeFor(double stopDist, out double effRisk, out double notional)
        {
            effRisk = 0; notional = 0;
            double eq = Compound ? Account.Equity : BaseEquity;
            double risk = eq * RiskPct / 100.0;
            if (stopDist <= 0) return 0;

            double units = Symbol.NormalizeVolumeInUnits(risk / stopDist, RoundingMode.Down);
            if (units < Symbol.VolumeInUnitsMin)
            {
                _belowMin++;
                Print(string.Format(
                    "[SIZE] volume below minimum: want {0:F2}, min {1:F2}. NO TRADE.",
                    risk / stopDist, Symbol.VolumeInUnitsMin));
                return 0;
            }
            effRisk = units * stopDist / eq * 100.0;          // what it ACTUALLY risks
            notional = units * Symbol.Bid / eq;
            return units;
        }

        // ------------------------- GUARDS ----------------------------
        // Run on EVERY tick regardless of whether a position is open: equity
        // crosses the target at the instant a winner closes.
        private void CheckGuards()
        {
            if (Account.Equity > _peakEquity) _peakEquity = Account.Equity;
            if (_halted) return;

            if (MaxDdPct > 0 && _peakEquity > 0)
            {
                double dd = (_peakEquity - Account.Equity) / _peakEquity * 100.0;
                if (dd >= MaxDdPct) { _halted = true; _haltReason = string.Format("MAX DD {0:F2}%", dd); }
            }
            if (!_halted && DayLossPct > 0 && _dayStartBal > 0)
            {
                double dl = (_dayStartBal - Account.Equity) / _dayStartBal * 100.0;
                if (dl >= DayLossPct) { _halted = true; _haltReason = string.Format("DAY LOSS {0:F2}%", dl); }
            }
            if (!_halted && ProfitPct > 0 && BaseEquity > 0)
            {
                double pr = (Account.Equity - BaseEquity) / BaseEquity * 100.0;
                if (pr >= ProfitPct) { _halted = true; _haltReason = string.Format("PROFIT {0:F2}%", pr); }
            }
            if (_halted)
            {
                Print("*** HALT: " + _haltReason + " *** flattening");
                Discord("**HALT** " + _haltReason);
                Flatten("HALT");
            }
        }

        // Friday flatten on the CLOCK, not on bar arrival: a quiet broker forms
        // no bar and the position survives the weekend into a gap.
        private void CheckFridayFlat()
        {
            if (!FridayFlat) return;
            var b = BrokerTime(Server.Time);
            if (b.DayOfWeek != DayOfWeek.Friday) return;
            if (MinOfDay(b) >= HmToMin(FridayFlatAt)) Flatten("FRIDAY");
        }

        private void Flatten(string reason)
        {
            foreach (var p in Positions.FindAll(Lbl, SymbolName).ToArray())
            {
                _forced = reason;
                var r = ClosePosition(p);
                if (!r.IsSuccessful)
                {
                    _forced = null;   // clear on FAILURE or it attaches to the next exit
                    Print("[close] failed: " + r.Error);
                }
            }
        }

        // ------------------------- LIFECYCLE -------------------------
        protected override void OnStart()
        {
            _pip = Symbol.TickSize;      // 3-digit XAGUSD: 1 pip = 1 tick = 0.001

            _s = new[]
            {
                new Sess { Name = "LONDON", AnchorMin = HmToMin(LondonAnchor),
                           EndMin = HmToMin(LondonEnd), Enabled = TradeLondon },
                new Sess { Name = "NY", AnchorMin = HmToMin(NyAnchor),
                           EndMin = HmToMin(NyEnd), Enabled = TradeNy }
            };

            _peakEquity = Account.Equity;
            _dayStartBal = Account.Balance;

            Positions.Closed += OnClosed;

            Print("==================================================================");
            Print("AUREON AXIS XAG  -  anchor / pivot / confirm / " + (Fade ? "FADE" : "WITH"));
            Print(string.Format("[SYMBOL] {0} digits {1} tick {2} pip {3} pipSize {4} volMin {5}",
                SymbolName, Symbol.Digits, Symbol.TickSize, _pip, Symbol.PipSize,
                Symbol.VolumeInUnitsMin));
            var bt = BrokerTime(Server.Time);
            Print(string.Format("[CLOCK ] mode {0}  UTC {1}  broker {2}  offset +{3}",
                Mode, Server.Time.ToString("yyyy-MM-dd HH:mm"),
                bt.ToString("yyyy-MM-dd HH:mm"), OffsetAt(Server.Time)));
            Print("[CLOCK ] VERIFY: the first ANCHOR row must read the intended "
                + "minute in broker time. An hour either side means the broker is not on EET.");
            Print(string.Format("[MECH  ] thr {0} / conf {1} / tp {2} / sl {3} pips at k=1  breakeven {4:F1}%",
                ThresholdPips, ConfPips, TargetPips, StopPips,
                StopPips / (StopPips + TargetPips) * 100.0));
            Print(string.Format("[REGIME] dyn {0}  floor {1:F2}  ADR{2}d / ref {3:F0} pips  clamp {4:F2}-{5:F2}",
                Dyn ? "ON" : "OFF", AdrFloor, AdrDays, AdrRefPips, DynMin, DynMax));

            // leverage is implied, not optional
            if (Symbol.Bid > 0)
            {
                double implied = RiskPct / 100.0 / (StopPips * _pip / Symbol.Bid);
                Print(string.Format("[LEVER ] {0:F2}% risk on a {1:F0}-pip stop at {2:F3} = {3:F1}x equity notional",
                    RiskPct, StopPips, Symbol.Bid, implied));
            }
            if (DayLossPct >= RiskPct * 2)
                Print(string.Format("*** WARNING: DayLossPct {0:F1}% >= 2x RiskPct {1:F1}%. One loss with "
                    + "stop overshoot can consume most of the daily brake.", DayLossPct, RiskPct));
            if (!Dyn || AdrFloor <= 0)
                Print("*** WARNING: the regime gate is LOAD-BEARING. Without it the measured "
                    + "edge was 60.5% win against a 68.7% breakeven.");
            Print("==================================================================");
        }

        protected override void OnTick()
        {
            CheckGuards();
            CheckFridayFlat();
        }

        protected override void OnBar()
        {
            int last = Bars.Count - 2;           // last CLOSED bar
            if (last < 1) return;

            DateTime bar = BrokerTime(Bars.OpenTimes[last]);
            double close = Bars.ClosePrices[last];

            if (bar.Date != _day)
            {
                if (_lastDayClose > 0)
                {
                    double gap = Math.Abs(close - _lastDayClose) / _lastDayClose * 100.0;
                    if (gap > 5.0)
                        Print(string.Format("*** DATA: day-rollover gap {0:F1}% ({1:F3} -> {2:F3}). Feed suspect.",
                            gap, _lastDayClose, close));
                    if (gap > _maxGap) _maxGap = gap;
                }
                NewDay(bar);
            }
            _lastDayClose = close;

            if (bar.DayOfWeek == DayOfWeek.Saturday || bar.DayOfWeek == DayOfWeek.Sunday) return;

            int mod = MinOfDay(bar);

            for (int i = 0; i < _s.Length; i++)
            {
                var s = _s[i];
                if (!s.Enabled) continue;

                // session end: flatten what this session owns
                if (mod >= s.EndMin)
                {
                    if (s.OwnerId.HasValue)
                    {
                        var p = Positions.FirstOrDefault(x => x.Id == s.OwnerId.Value);
                        if (p != null) { _forced = "EOD"; var r = ClosePosition(p); if (!r.IsSuccessful) _forced = null; }
                    }
                    s.State = St.Done;
                    continue;
                }

                // ---- ANCHOR ----
                if (s.State == St.Idle && mod == s.AnchorMin)
                {
                    if (!_dayOk) { s.State = St.Done; continue; }
                    s.Anchor = close;
                    s.AnchorTime = bar;
                    s.State = St.Anchored;
                    _anchored++;
                    Print(string.Format("[ANCHOR] {0} {1} broker  anchor {2:F3}  k={3:F2}  pivot needs {4:F3}",
                        s.Name, bar.ToString("HH:mm"), close, _k,
                        close + ThresholdPips * _pip * _k));
                    continue;
                }

                // ---- PIVOT (observation only, nothing is placed) ----
                if (s.State == St.Anchored)
                {
                    int el = (int)(bar - s.AnchorTime).TotalMinutes;
                    if (el > PivotWindow) { s.State = St.Done; _blkNoPivot++; continue; }
                    double disp = close - s.Anchor;
                    double thr = ThresholdPips * _pip * _k;
                    bool up = disp >= thr;
                    bool dn = TwoWay && disp <= -thr;
                    if (up || dn)
                    {
                        if (MaxDispPips > 0 && Math.Abs(disp) > MaxDispPips * _pip * _k)
                        {
                            s.State = St.Done; _blkOvershoot++;
                            Say(string.Format("[PIVOT ] {0} overshoot {1:F0} pips > cap - session dead",
                                s.Name, Math.Abs(disp) / _pip));
                            continue;
                        }
                        s.Pivot = close;
                        s.PivotTime = bar;
                        s.State = St.Pivoted;
                        _pivotedN++;
                        Print(string.Format("[PIVOT ] {0} {1}  pivot {2:F3} ({3:F0} pips from anchor, {4} min)  watching +/-{5:F0} pips",
                            s.Name, bar.ToString("HH:mm"), close, disp / _pip, el, ConfPips * _k));
                    }
                    continue;
                }

                // ---- CONFIRM -> ENTRY ----
                if (s.State == St.Pivoted)
                {
                    int el = (int)(bar - s.PivotTime).TotalMinutes;
                    if (el > ConfWindow) { s.State = St.Done; _blkNoConf++; continue; }
                    double conf = ConfPips * _pip * _k;
                    double rel = close - s.Pivot;
                    int dir = 0;
                    if (rel >= conf) dir = Fade ? -1 : 1;
                    else if (rel <= -conf) dir = Fade ? 1 : -1;
                    if (dir == 0) continue;

                    _confirmedN++;

                    if (_halted) { _blkHalt++; s.State = St.Done; continue; }
                    if (s.Chips >= 1 || _ownerId.HasValue) { _blkChip++; s.State = St.Done; continue; }
                    int left = s.EndMin - mod;
                    if (left < MinLifeMin)
                    {
                        _blkLife++; s.State = St.Done;
                        Say(string.Format("[BLOCK ] {0} confirm with only {1} min left - no trade", s.Name, left));
                        continue;
                    }
                    Print(string.Format("[CONFIRM] {0} {1}  moved {2:+0;-0} pips off pivot -> {3}",
                        s.Name, bar.ToString("HH:mm"), rel / _pip, dir > 0 ? "BUY" : "SELL"));
                    Place(i, dir, bar);
                }
            }
        }

        private void NewDay(DateTime bar)
        {
            _day = bar.Date;
            _dayStartBal = Account.Balance;   // prop firms measure the day off balance
            _halted = false;
            _haltReason = "";
            foreach (var s in _s)
            {
                s.State = St.Idle; s.Chips = 0; s.OwnerId = null;
                s.Anchor = 0; s.Pivot = 0;
            }

            _k = 1.0;
            _dayOk = true;
            if (!Dyn && AdrFloor <= 0) return;

            double adr = PriorAdr(bar);
            if (adr <= 0)
            {
                _dayOk = false; _blkWarm++;
                Say(string.Format("[DAY {0:yyyy-MM-dd}] no ADR yet (warmup) - no trading", bar));
                return;
            }
            double ratio = adr / (AdrRefPips * _pip);
            if (AdrFloor > 0 && ratio < AdrFloor)
            {
                _dayOk = false; _blkAdr++;
                Say(string.Format("[DAY {0:yyyy-MM-dd}] ADR {1:F0} pips = {2:F2}x ref, below floor {3:F2} -> NO TRADING",
                    bar, adr / _pip, ratio, AdrFloor));
                return;
            }
            if (Dyn) _k = Math.Min(DynMax, Math.Max(DynMin, ratio));
            Say(string.Format("[DAY {0:yyyy-MM-dd}] ADR {1:F0} pips = {2:F2}x ref -> k={3:F2}  thr {4:F0} conf {5:F0} tp {6:F0} sl {7:F0}",
                bar, adr / _pip, ratio, _k, ThresholdPips * _k, ConfPips * _k,
                TargetPips * _k, StopPips * _k));
        }

        private void Place(int idx, int dir, DateTime bar)
        {
            double stopDist = StopPips * _pip * _k;
            double tgtDist = TargetPips * _pip * _k;

            double effRisk, notional;
            double units = VolumeFor(stopDist, out effRisk, out notional);
            if (units <= 0) return;

            double slPips = stopDist / Symbol.PipSize;
            double tpPips = tgtDist / Symbol.PipSize;

            var side = dir > 0 ? TradeType.Buy : TradeType.Sell;
            var r = ExecuteMarketOrder(side, SymbolName, units, Lbl, slPips, tpPips);
            if (!r.IsSuccessful || r.Position == null)
            {
                _rejected++;
                Print(string.Format("[ORDER REJECTED] {0} {1:F2} units: {2}", side, units, r.Error));
                return;
            }

            // ---- COUNT THE CHIP AT PLACEMENT, never at close ----
            _ownerId = r.Position.Id;
            _ownerIdx = idx;
            _s[idx].OwnerId = r.Position.Id;
            _s[idx].Chips++;
            if (_s[idx].Chips == 1) _sessTraded++;
            _s[idx].State = St.Done;

            _openDir = dir;
            _openEntry = r.Position.EntryPrice;
            _openSl = r.Position.StopLoss ?? (dir > 0 ? _openEntry - stopDist : _openEntry + stopDist);
            _openTp = r.Position.TakeProfit ?? (dir > 0 ? _openEntry + tgtDist : _openEntry - tgtDist);
            _openTime = bar;

            _tradedN++;
            _riskSum += effRisk;
            if (effRisk < _riskMin) _riskMin = effRisk;
            if (effRisk > _riskMax) _riskMax = effRisk;
            if (notional > _maxNotional) _maxNotional = notional;

            string m = string.Format(
                "[ENTRY] {0} {1} {2:F2} units @ {3:F3}  sl {4:F3} tp {5:F3}  chip {6}/1  k={7:F2}  effRisk {8:F2}% (cfg {9:F2}%)  notional {10:F1}x",
                _s[idx].Name, side, units, _openEntry, _openSl, _openTp,
                _s[idx].Chips, _k, effRisk, RiskPct, notional);
            Print(m);
            Discord(m);

            if (Math.Abs(effRisk - RiskPct) > RiskPct * 0.10)
                Print(string.Format("*** SIZING WARNING: taken {0:F2}% vs configured {1:F2}% (>10% divergence)",
                    effRisk, RiskPct));
        }

        private void OnClosed(PositionClosedEventArgs args)
        {
            var p = args.Position;
            if (p.Label != Lbl || p.SymbolName != SymbolName) return;
            if (!_ownerId.HasValue || p.Id != _ownerId.Value)
            {
                // not ours (adopted orphan, manual close) - still clear any stale reason
                _forced = null;
                return;
            }

            double exit = ExitPriceOf(p);

            // exit label from the STORED order prices, never inferred from the move
            string reason;
            double ts = Symbol.TickSize;
            if (_forced != null) { reason = _forced; _forcedN++; }
            else if (_openDir > 0 ? exit >= _openTp - ts : exit <= _openTp + ts)
            { reason = "TARGET"; _tpN++; _tpOver += Math.Abs(exit - _openTp) / _pip; }
            else if (_openDir > 0 ? exit <= _openSl + ts : exit >= _openSl - ts)
            { reason = "STOP"; _slN++; _slOver += Math.Abs(exit - _openSl) / _pip; }
            else { reason = "OTHER"; _otherN++; }

            double net = p.NetProfit;
            _pnl += net;
            _closed++;
            if (net > 0) _wins++;

            string m = string.Format(
                "[EXIT ] {0} {1} @ {2:F3}  {3}  {4:+0;-0} pips  ${5:+0.00;-0.00}  (win {6}/{7} = {8:F1}%)",
                _ownerIdx >= 0 ? _s[_ownerIdx].Name : "?",
                p.TradeType, exit, reason, (exit - _openEntry) * _openDir / _pip, net,
                _wins, _closed, _closed > 0 ? 100.0 * _wins / _closed : 0);
            Print(m);
            Discord(m);

            _forced = null;
            if (_ownerIdx >= 0) _s[_ownerIdx].OwnerId = null;
            _ownerId = null;
            _ownerIdx = -1;
        }

        // The REAL fill, from trade history. Never derive it from the P/L and
        // never infer the exit reason from the size of the move.
        private double ExitPriceOf(Position p)
        {
            var h = History.LastOrDefault(x => x.PositionId == p.Id);
            if (h != null && h.ClosingPrice > 0) return h.ClosingPrice;

            // fallback only: reconstruct from pips moved
            if (Symbol.PipValue > 0 && p.VolumeInUnits > 0)
            {
                double pips = p.GrossProfit / (Symbol.PipValue * p.VolumeInUnits);
                int dir = p.TradeType == TradeType.Buy ? 1 : -1;
                return p.EntryPrice + pips * Symbol.PipSize * dir;
            }
            return p.EntryPrice;
        }

        protected override void OnStop()
        {
            double wr = _closed > 0 ? 100.0 * _wins / _closed : 0;
            double be = StopPips / (StopPips + TargetPips) * 100.0;
            double ratio = _sessTraded > 0 ? (double)_tradedN / _sessTraded : 0;

            Print("==================================================================");
            Print("AUREON AXIS XAG  -  RUN REPORT");
            Print("==================================================================");
            Print(string.Format("FUNNEL       anchored {0}  pivoted {1}  confirmed {2}  traded {3}",
                _anchored, _pivotedN, _confirmedN, _tradedN));
            Print(string.Format("BLOCKED      adr-floor {0}  warmup {1}  no-pivot {2}  no-confirm {3}",
                _blkAdr, _blkWarm, _blkNoPivot, _blkNoConf));
            Print(string.Format("BLOCKED      chip-cap {0}  min-life {1}  halted {2}  overshoot {3}",
                _blkChip, _blkLife, _blkHalt, _blkOvershoot));
            Print(string.Format("GATE 1       trades / sessions traded = {0:F2}  {1}",
                ratio, Math.Abs(ratio - 1.0) < 0.001 ? "OK" : "*** HARD STOP: chip cap is broken ***"));
            Print(string.Format("GATE 2       rejected {0}  below-min {1}  {2}",
                _rejected, _belowMin,
                (_rejected == 0 && _belowMin == 0) ? "OK"
                : "*** trade count is short - P/L is NOT a strategy result ***"));
            Print(string.Format("SIZING       configured {0:F2}%  taken mean {1:F2}% min {2:F2}% max {3:F2}%",
                RiskPct, _tradedN > 0 ? _riskSum / _tradedN : 0,
                _riskMin < double.MaxValue ? _riskMin : 0, _riskMax));
            Print(string.Format("SIZING       largest notional {0:F1}x equity", _maxNotional));
            Print(string.Format("CLOCK        mode {0}  offset +{1}  DST switches crossed {2}",
                Mode, _lastOffset, _switches.Count));
            foreach (var sw in _switches) Print("             " + sw);
            Print(string.Format("EXIT FIDELITY target overshoot {0:F2} pips (n={1})  stop overshoot {2:F2} pips (n={3})",
                _tpN > 0 ? _tpOver / _tpN : 0, _tpN, _slN > 0 ? _slOver / _slN : 0, _slN));
            Print(string.Format("EXITS        TARGET {0}  STOP {1}  forced {2}  OTHER {3}",
                _tpN, _slN, _forcedN, _otherN));
            Print(string.Format("RESULT       trades {0}  win {1:F1}%  breakeven {2:F1}%  {3}",
                _closed, wr, be, wr > be ? "ABOVE" : "BELOW"));
            Print(string.Format("RESULT       net ${0:F2}  (${1:F2} per trade)",
                _pnl, _closed > 0 ? _pnl / _closed : 0));
            Print(string.Format("DATA         largest day-rollover gap {0:F1}%", _maxGap));
            if (_halted) Print("HALTED       " + _haltReason);
            Print("------------------------------------------------------------------");
            Print("Read GATE 1 and GATE 2 before RESULT. A broken chip cap or a short");
            Print("trade count means the P/L describes something other than the strategy.");
            Print("==================================================================");
        }
    }
}